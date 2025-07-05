using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebSockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace PrintBridgeTrayApp;

public class WebServer
{
    private WebApplication? app;
    private readonly PrintService printService;
    private readonly PrintJobLog jobLog;

    public WebServer()
    {
        printService = new PrintService();
        jobLog = new PrintJobLog();
    }

    public void Start()
    {
        var builder = WebApplication.CreateBuilder();
        
        // Add services
        builder.Services.AddCors();
        builder.Services.AddWebSocketServerConnectionManager();

        app = builder.Build();

        // Configure middleware
        app.UseCors(x => x.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
        app.UseWebSockets();

        // Routes
        app.MapGet("/", ServeDashboard);
        app.MapPost("/print", HandlePrintRequest);
        app.MapGet("/ws", HandleWebSocket);
        app.MapGet("/printers", GetPrinters);
        app.MapGet("/jobs", GetJobs);

        Console.WriteLine("Starting web server on http://localhost:8080");
        app.Run("http://localhost:8080");
    }

    public void Stop()
    {
        app?.StopAsync().Wait();
    }

    private async Task ServeDashboard(HttpContext context)
    {
        context.Response.ContentType = "text/html";
        var html = GetDashboardHtml();
        await context.Response.WriteAsync(html);
    }

    private async Task HandlePrintRequest(HttpContext context)
    {
        try
        {
            using var reader = new StreamReader(context.Request.Body);
            var json = await reader.ReadToEndAsync();
            
            var request = JsonSerializer.Deserialize<PrintRequest>(json);
            if (request?.Base64Image == null)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync("{\"error\": \"base64Image is required\"}");
                return;
            }

            var result = await printService.PrintImageAsync(request.Base64Image, request.PrinterName);
            
            // Log the job
            jobLog.AddJob(new PrintJob
            {
                Timestamp = DateTime.Now,
                PrinterName = result.PrinterName,
                Success = result.Success,
                ErrorMessage = result.ErrorMessage
            });

            var response = JsonSerializer.Serialize(result);
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(response);
        }
        catch (Exception ex)
        {
            context.Response.StatusCode = 500;
            await context.Response.WriteAsync($"{{\"error\": \"{ex.Message}\"}}");
        }
    }

    private async Task HandleWebSocket(HttpContext context)
    {
        if (context.WebSockets.IsWebSocketRequest)
        {
            using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
            await HandleWebSocketConnection(webSocket);
        }
        else
        {
            context.Response.StatusCode = 400;
        }
    }

    private async Task HandleWebSocketConnection(WebSocket webSocket)
    {
        var buffer = new byte[1024 * 1024]; // 1MB buffer
        var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);

        while (!result.CloseStatus.HasValue)
        {
            if (result.MessageType == WebSocketMessageType.Text)
            {
                var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                
                try
                {
                    var printResult = await printService.PrintImageAsync(message, null);
                    
                    // Log the job
                    jobLog.AddJob(new PrintJob
                    {
                        Timestamp = DateTime.Now,
                        PrinterName = printResult.PrinterName,
                        Success = printResult.Success,
                        ErrorMessage = printResult.ErrorMessage
                    });

                    var response = JsonSerializer.Serialize(printResult);
                    var responseBytes = Encoding.UTF8.GetBytes(response);
                    await webSocket.SendAsync(new ArraySegment<byte>(responseBytes), WebSocketMessageType.Text, true, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    var errorResponse = JsonSerializer.Serialize(new { error = ex.Message });
                    var errorBytes = Encoding.UTF8.GetBytes(errorResponse);
                    await webSocket.SendAsync(new ArraySegment<byte>(errorBytes), WebSocketMessageType.Text, true, CancellationToken.None);
                }
            }

            result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
        }

        await webSocket.CloseAsync(result.CloseStatus.Value, result.CloseStatusDescription, CancellationToken.None);
    }

    private async Task GetPrinters(HttpContext context)
    {
        var printers = printService.GetAvailablePrinters();
        var json = JsonSerializer.Serialize(printers);
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(json);
    }

    private async Task GetJobs(HttpContext context)
    {
        var jobs = jobLog.GetJobs();
        var json = JsonSerializer.Serialize(jobs);
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(json);
    }

    private string GetDashboardHtml()
    {
        return @"
<!DOCTYPE html>
<html>
<head>
    <title>PrintBridge Dashboard</title>
    <style>
        body { font-family: Arial, sans-serif; margin: 20px; background: #f5f5f5; }
        .container { max-width: 1200px; margin: 0 auto; background: white; padding: 20px; border-radius: 8px; box-shadow: 0 2px 10px rgba(0,0,0,0.1); }
        .section { margin-bottom: 30px; padding: 20px; border: 1px solid #ddd; border-radius: 5px; }
        .section h2 { margin-top: 0; color: #333; }
        .printer-list { display: grid; grid-template-columns: repeat(auto-fill, minmax(200px, 1fr)); gap: 10px; }
        .printer-item { padding: 10px; background: #f8f9fa; border: 1px solid #dee2e6; border-radius: 4px; }
        .preview { max-width: 100%; max-height: 400px; border: 1px solid #ddd; border-radius: 4px; }
        .job-log { max-height: 300px; overflow-y: auto; }
        .job-item { padding: 8px; margin: 4px 0; border-radius: 4px; }
        .job-success { background: #d4edda; border: 1px solid #c3e6cb; }
        .job-error { background: #f8d7da; border: 1px solid #f5c6cb; }
        .status { padding: 10px; border-radius: 4px; margin-bottom: 20px; }
        .status.connected { background: #d4edda; color: #155724; }
        .status.disconnected { background: #f8d7da; color: #721c24; }
        button { padding: 8px 16px; background: #007bff; color: white; border: none; border-radius: 4px; cursor: pointer; }
        button:hover { background: #0056b3; }
        input, select { padding: 8px; border: 1px solid #ddd; border-radius: 4px; margin: 5px; }
    </style>
</head>
<body>
    <div class='container'>
        <h1>PrintBridge Dashboard</h1>
        
        <div id='status' class='status disconnected'>WebSocket: Disconnected</div>
        
        <div class='section'>
            <h2>Available Printers</h2>
            <div id='printers' class='printer-list'></div>
        </div>
        
        <div class='section'>
            <h2>Last Received Image</h2>
            <img id='preview' class='preview' style='display: none;' />
            <p id='no-image'>No image received yet.</p>
        </div>
        
        <div class='section'>
            <h2>Print Job Log</h2>
            <div id='jobs' class='job-log'></div>
        </div>
    </div>

    <script>
        let ws = null;
        let lastImage = null;

        function connectWebSocket() {
            ws = new WebSocket('ws://localhost:8080/ws');
            
            ws.onopen = function() {
                document.getElementById('status').className = 'status connected';
                document.getElementById('status').textContent = 'WebSocket: Connected';
            };
            
            ws.onmessage = function(event) {
                const data = JSON.parse(event.data);
                if (data.base64Image) {
                    lastImage = data.base64Image;
                    updatePreview();
                }
            };
            
            ws.onclose = function() {
                document.getElementById('status').className = 'status disconnected';
                document.getElementById('status').textContent = 'WebSocket: Disconnected';
                setTimeout(connectWebSocket, 3000);
            };
            
            ws.onerror = function() {
                document.getElementById('status').className = 'status disconnected';
                document.getElementById('status').textContent = 'WebSocket: Error';
            };
        }

        function updatePreview() {
            if (lastImage) {
                const img = document.getElementById('preview');
                img.src = 'data:image/png;base64,' + lastImage;
                img.style.display = 'block';
                document.getElementById('no-image').style.display = 'none';
            }
        }

        function loadPrinters() {
            fetch('/printers')
                .then(response => response.json())
                .then(printers => {
                    const container = document.getElementById('printers');
                    container.innerHTML = '';
                    printers.forEach(printer => {
                        const div = document.createElement('div');
                        div.className = 'printer-item';
                        div.textContent = printer;
                        container.appendChild(div);
                    });
                });
        }

        function loadJobs() {
            fetch('/jobs')
                .then(response => response.json())
                .then(jobs => {
                    const container = document.getElementById('jobs');
                    container.innerHTML = '';
                    jobs.reverse().forEach(job => {
                        const div = document.createElement('div');
                        div.className = 'job-item ' + (job.success ? 'job-success' : 'job-error');
                        div.innerHTML = `
                            <strong>${new Date(job.timestamp).toLocaleString()}</strong><br>
                            Printer: ${job.printerName}<br>
                            Status: ${job.success ? 'Success' : 'Error'}<br>
                            ${job.errorMessage ? 'Error: ' + job.errorMessage : ''}
                        `;
                        container.appendChild(div);
                    });
                });
        }

        // Initialize
        connectWebSocket();
        loadPrinters();
        loadJobs();
        
        // Refresh data every 5 seconds
        setInterval(() => {
            loadPrinters();
            loadJobs();
        }, 5000);
    </script>
</body>
</html>";
    }
}

public class PrintRequest
{
    public string? Base64Image { get; set; }
    public string? PrinterName { get; set; }
} 
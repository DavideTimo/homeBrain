using System.Net.Http;

namespace CasaTimo.Workers;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly HttpClient _httpClient;

    public Worker(ILogger<Worker> logger, HttpClient httpClient)
    {
        _logger = logger;
        _httpClient = httpClient;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var client = _httpClient;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var resp = await client.GetAsync("/health", stoppingToken);
                if (resp.IsSuccessStatusCode)
                {
                    var json = await resp.Content.ReadAsStringAsync(stoppingToken);
                    using var doc = System.Text.Json.JsonDocument.Parse(json);
                    var status = doc.RootElement.GetProperty("status").GetString();
                    var uptime = doc.RootElement.GetProperty("uptime").GetString();
                    _logger.LogInformation("API health: {status}, uptime: {uptime}", status, uptime);
                }
                else
                {
                    _logger.LogWarning("Health check returned non-success: {code}", resp.StatusCode);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // shutting down
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error calling API health endpoint");
            }

            // Also keep existing heartbeat log
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Worker heartbeat at: {time}", DateTimeOffset.Now);
            }

            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
    }
}

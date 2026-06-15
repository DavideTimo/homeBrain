using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CasaTimo.Infrastructure.Connectors;

public class ViessmannConnector : IHostedService, IDisposable
{
    private readonly ILogger<ViessmannConnector> _logger;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ViessmannOptions _options = new();
    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    public ViessmannConnector(IConfiguration configuration, ILogger<ViessmannConnector> logger, IHttpClientFactory httpFactory)
    {
        _logger = logger;
        _httpFactory = httpFactory;
        configuration.GetSection("Viessmann").Bind(_options);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(_options.ApiBaseUrl))
        {
            _logger.LogInformation("ViessmannConnector: no ApiBaseUrl configured, skipping");
            return Task.CompletedTask;
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loopTask = RunLoopAsync(_cts.Token);
        return Task.CompletedTask;
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                string? token = null;
                if (!string.IsNullOrEmpty(_options.ClientId) && !string.IsNullOrEmpty(_options.ClientSecret))
                    token = await GetTokenClientCredentialsAsync(ct);
                else if (!string.IsNullOrEmpty(_options.Username) && !string.IsNullOrEmpty(_options.Password))
                    token = await GetTokenPasswordAsync(ct);

                var client = _httpFactory.CreateClient("viessmann");
                if (!string.IsNullOrEmpty(token))
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

                var url = _options.ApiBaseUrl!.TrimEnd('/') + "/";
                _logger.LogInformation("ViessmannConnector: polling {url}", url);
                var resp = await client.GetAsync(url, ct);
                var content = await resp.Content.ReadAsStringAsync(ct);
                var snippet = content.Length > 400 ? content[..400] + "..." : content;
                _logger.LogInformation("ViessmannConnector: {status} — {snippet}", resp.StatusCode, snippet);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ViessmannConnector poll error");
            }

            await Task.Delay(TimeSpan.FromSeconds(_options.PollIntervalSeconds), ct).ConfigureAwait(false);
        }
    }

    private async Task<string?> GetTokenClientCredentialsAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_options.TokenEndpoint)) return null;
        try
        {
            var client = _httpFactory.CreateClient();
            var pairs = new List<KeyValuePair<string, string>>
            {
                new("grant_type", "client_credentials"),
                new("client_id", _options.ClientId!),
                new("client_secret", _options.ClientSecret!)
            };
            var resp = await client.PostAsync(_options.TokenEndpoint, new FormUrlEncodedContent(pairs), ct);
            resp.EnsureSuccessStatusCode();
            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            return doc.RootElement.TryGetProperty("access_token", out var t) ? t.GetString() : null;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Token request failed (client_credentials)");
            return null;
        }
    }

    private async Task<string?> GetTokenPasswordAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_options.TokenEndpoint)) return null;
        try
        {
            var client = _httpFactory.CreateClient();
            var pairs = new List<KeyValuePair<string, string>>
            {
                new("grant_type", "password"),
                new("username", _options.Username!),
                new("password", _options.Password!)
            };
            if (!string.IsNullOrEmpty(_options.ClientId)) pairs.Add(new("client_id", _options.ClientId));
            var resp = await client.PostAsync(_options.TokenEndpoint, new FormUrlEncodedContent(pairs), ct);
            resp.EnsureSuccessStatusCode();
            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            return doc.RootElement.TryGetProperty("access_token", out var t) ? t.GetString() : null;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Token request failed (password)");
            return null;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        if (_loopTask != null)
        {
            try { await _loopTask.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) { }
        }
    }

    public void Dispose()
    {
        _cts?.Dispose();
    }
}

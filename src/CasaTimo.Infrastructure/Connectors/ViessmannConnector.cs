using System.Net.Http;
using System.Net.Http.Headers;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CasaTimo.Infrastructure.Connectors;

/// <summary>
/// Lightweight Viessmann connector scaffold. Performs OAuth token exchange
/// (client_credentials or password) when configured, then polls the configured
/// API base and logs a short response. Not registered automatically; register
/// in the host where needed and provide credentials in configuration.
/// </summary>
public class ViessmannConnector : IHostedService, IDisposable
{
    private readonly ILogger<ViessmannConnector> _logger;
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpFactory;
    private ViessmannOptions _options = new();
    private CancellationTokenSource? _cts;

    public ViessmannConnector(IConfiguration configuration, ILogger<ViessmannConnector> logger, IHttpClientFactory httpFactory)
    {
        _configuration = configuration;
        _logger = logger;
        _httpFactory = httpFactory;
        var section = configuration.GetSection("Viessmann");
        section.Bind(_options);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(_options.ApiBaseUrl))
        {
            _logger.LogInformation("ViessmannConnector: no ApiBaseUrl configured, skipping.");
            return Task.CompletedTask;
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ = RunLoopAsync(_cts.Token);
        return Task.CompletedTask;
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                string? token = null;
                if (!string.IsNullOrEmpty(_options.ClientId) && !string.IsNullOrEmpty(_options.ClientSecret) && !string.IsNullOrEmpty(_options.TokenEndpoint))
                {
                    token = await GetTokenClientCredentialsAsync(ct);
                }
                else if (!string.IsNullOrEmpty(_options.Username) && !string.IsNullOrEmpty(_options.Password) && !string.IsNullOrEmpty(_options.TokenEndpoint))
                {
                    token = await GetTokenPasswordAsync(ct);
                }

                var client = _httpFactory.CreateClient("viessmann");
                if (!string.IsNullOrEmpty(token)) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

                var url = _options.ApiBaseUrl!.TrimEnd('/') + "/";
                _logger.LogInformation("ViessmannConnector: requesting {url}", url);
                var resp = await client.GetAsync(url, ct);
                var content = await resp.Content.ReadAsStringAsync(ct);
                var snippet = content?.Length > 400 ? content.Substring(0, 400) + "..." : content;
                _logger.LogInformation("ViessmannConnector: {status}. Response snippet: {snippet}", resp.StatusCode, snippet);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ViessmannConnector error");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_options.PollIntervalSeconds), ct);
            }
            catch (TaskCanceledException) { break; }
        }
    }

    private async Task<string?> GetTokenClientCredentialsAsync(CancellationToken ct)
    {
        try
        {
            var client = _httpFactory.CreateClient();
            var pairs = new List<KeyValuePair<string,string>>
            {
                new("grant_type", "client_credentials"),
                new("client_id", _options.ClientId!),
                new("client_secret", _options.ClientSecret!)
            };
            var resp = await client.PostAsync(_options.TokenEndpoint!, new FormUrlEncodedContent(pairs), ct);
            resp.EnsureSuccessStatusCode();
            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (doc.RootElement.TryGetProperty("access_token", out var t)) return t.GetString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Token request failed (client_credentials)");
        }
        return null;
    }

    private async Task<string?> GetTokenPasswordAsync(CancellationToken ct)
    {
        try
        {
            var client = _httpFactory.CreateClient();
            var pairs = new List<KeyValuePair<string,string>>
            {
                new("grant_type", "password"),
                new("username", _options.Username!),
                new("password", _options.Password!)
            };
            if (!string.IsNullOrEmpty(_options.ClientId)) pairs.Add(new("client_id", _options.ClientId));
            var resp = await client.PostAsync(_options.TokenEndpoint!, new FormUrlEncodedContent(pairs), ct);
            resp.EnsureSuccessStatusCode();
            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (doc.RootElement.TryGetProperty("access_token", out var t)) return t.GetString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Token request failed (password)");
        }
        return null;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _cts?.Dispose();
    }
}

using System.Net.Http.Headers;
using System.Text.Json;
using CasaTimo.Infrastructure.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CasaTimo.Infrastructure.Connectors;

public class ViessmannConnector : IHostedService, IDisposable
{
    private readonly ILogger<ViessmannConnector> _logger;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IMessageBroker _broker;
    private readonly ViessmannOptions _options = new();
    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    // Cached after first discovery
    private string? _installationId;
    private string? _gatewaySerial;

    public ViessmannConnector(
        IConfiguration configuration,
        ILogger<ViessmannConnector> logger,
        IHttpClientFactory httpFactory,
        IMessageBroker broker)
    {
        _logger = logger;
        _httpFactory = httpFactory;
        _broker = broker;
        configuration.GetSection("Viessmann").Bind(_options);

        // Pre-seed from config if provided
        _installationId = _options.InstallationId;
        _gatewaySerial = _options.GatewaySerial;
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

                await PollFeaturesAsync(client, ct);
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

    private async Task PollFeaturesAsync(HttpClient client, CancellationToken ct)
    {
        var baseUrl = _options.ApiBaseUrl!.TrimEnd('/');

        // Discover installation and gateway if not yet known
        if (string.IsNullOrEmpty(_installationId) || string.IsNullOrEmpty(_gatewaySerial))
        {
            var discovered = await DiscoverInstallationAsync(client, baseUrl, ct);
            if (!discovered)
            {
                _logger.LogWarning("ViessmannConnector: could not discover installation/gateway, skipping poll");
                return;
            }
        }

        var featuresUrl = $"{baseUrl}/iot/v1/equipment/installations/{_installationId}/gateways/{_gatewaySerial}/devices/0/features";
        _logger.LogInformation("ViessmannConnector: fetching features from {url}", featuresUrl);

        var resp = await client.GetAsync(featuresUrl, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("ViessmannConnector: features request returned {status}: {body}", resp.StatusCode, body.Length > 300 ? body[..300] : body);
            return;
        }

        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        if (!doc.RootElement.TryGetProperty("data", out var dataArr))
        {
            _logger.LogWarning("ViessmannConnector: features response has no 'data' array");
            return;
        }

        // Build lookup: feature name -> properties element
        var featureMap = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in dataArr.EnumerateArray())
        {
            if (item.TryGetProperty("feature", out var featureEl) &&
                item.TryGetProperty("properties", out var propsEl))
            {
                var featureName = featureEl.GetString();
                if (!string.IsNullOrEmpty(featureName))
                    featureMap[featureName] = propsEl;
            }
        }

        // Publish numeric sensor readings
        await PublishNumericAsync(featureMap, "heating.sensors.temperature.outside",
            "value", "casatimo/pdc/temperature/outside", "°C", ct);

        await PublishNumericAsync(featureMap, "heating.circuits.0.sensors.temperature.supply",
            "value", "casatimo/pdc/temperature/supply", "°C", ct);

        await PublishNumericAsync(featureMap, "heating.dhw.sensors.temperature.hotWaterStorage",
            "value", "casatimo/pdc/dhw/temperature", "°C", ct);

        await PublishNumericAsync(featureMap, "heating.compressors.0.statistics.hours",
            "value", "casatimo/pdc/compressor/hours", "h", ct);

        // Publish string mode
        await PublishStringAsync(featureMap, "heating.circuits.0.operating.modes.active",
            "value", "casatimo/pdc/mode", ct);
    }

    private async Task<bool> DiscoverInstallationAsync(HttpClient client, string baseUrl, CancellationToken ct)
    {
        try
        {
            var installUrl = $"{baseUrl}/iot/v1/equipment/installations?includeGateways=true";
            _logger.LogInformation("ViessmannConnector: discovering installation from {url}", installUrl);

            var resp = await client.GetAsync(installUrl, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("ViessmannConnector: installations request returned {status}", resp.StatusCode);
                return false;
            }

            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            // Response shape: { "data": [ { "id": 123, "gateways": [ { "serial": "..." } ] } ] }
            if (!doc.RootElement.TryGetProperty("data", out var dataArr))
                return false;

            foreach (var installation in dataArr.EnumerateArray())
            {
                if (!installation.TryGetProperty("id", out var idEl))
                    continue;

                _installationId = idEl.ValueKind == JsonValueKind.Number
                    ? idEl.GetInt64().ToString()
                    : idEl.GetString();

                if (installation.TryGetProperty("gateways", out var gwArr))
                {
                    foreach (var gw in gwArr.EnumerateArray())
                    {
                        if (gw.TryGetProperty("serial", out var serialEl))
                        {
                            _gatewaySerial = serialEl.GetString();
                            break;
                        }
                    }
                }

                if (!string.IsNullOrEmpty(_installationId) && !string.IsNullOrEmpty(_gatewaySerial))
                {
                    _logger.LogInformation("ViessmannConnector: discovered installationId={id} gatewaySerial={serial}",
                        _installationId, _gatewaySerial);
                    return true;
                }
            }

            _logger.LogWarning("ViessmannConnector: no installation/gateway found in response");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ViessmannConnector: discovery failed");
            return false;
        }
    }

    private async Task PublishNumericAsync(
        Dictionary<string, JsonElement> featureMap,
        string featureName,
        string propertyName,
        string topic,
        string unit,
        CancellationToken ct)
    {
        if (!featureMap.TryGetValue(featureName, out var props))
        {
            _logger.LogDebug("ViessmannConnector: feature '{feature}' not found, skipping", featureName);
            return;
        }

        if (!props.TryGetProperty(propertyName, out var propEl) ||
            !propEl.TryGetProperty("value", out var valueEl))
        {
            _logger.LogDebug("ViessmannConnector: property '{prop}' not found in feature '{feature}'", propertyName, featureName);
            return;
        }

        double numericValue;
        if (valueEl.ValueKind == JsonValueKind.Number)
            numericValue = valueEl.GetDouble();
        else
        {
            _logger.LogDebug("ViessmannConnector: value of '{feature}'.'{prop}' is not a number", featureName, propertyName);
            return;
        }

        var payload = numericValue.ToString("G6", System.Globalization.CultureInfo.InvariantCulture);
        await _broker.PublishAsync(topic, payload, ct);
        _logger.LogInformation("ViessmannConnector: published {topic} = {value} {unit}", topic, payload, unit);
    }

    private async Task PublishStringAsync(
        Dictionary<string, JsonElement> featureMap,
        string featureName,
        string propertyName,
        string topic,
        CancellationToken ct)
    {
        if (!featureMap.TryGetValue(featureName, out var props))
        {
            _logger.LogDebug("ViessmannConnector: feature '{feature}' not found, skipping", featureName);
            return;
        }

        if (!props.TryGetProperty(propertyName, out var propEl) ||
            !propEl.TryGetProperty("value", out var valueEl))
        {
            _logger.LogDebug("ViessmannConnector: property '{prop}' not found in feature '{feature}'", propertyName, featureName);
            return;
        }

        var stringValue = valueEl.GetString() ?? valueEl.ToString();
        await _broker.PublishAsync(topic, stringValue, ct);
        _logger.LogInformation("ViessmannConnector: published {topic} = {value}", topic, stringValue);
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

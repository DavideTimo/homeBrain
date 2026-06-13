using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CasaTimo.Core.Interfaces;
using CasaTimo.Core.Models;

namespace CasaTimo.Workers;

public class ViessmannConnector : BackgroundService
{
    private readonly IMqttService _mqtt;
    private readonly IConfiguration _config;
    private readonly ILogger<ViessmannConnector> _logger;
    private readonly HttpClient _http;

    private string? _accessToken;
    private string? _refreshToken;
    private DateTime _tokenExpiry = DateTime.MinValue;

    private const string AuthUrl = "https://iam.viessmann.com/idp/v3/token";
    private const string ApiBase = "https://api.viessmann.com/iot/v1";

    public ViessmannConnector(IMqttService mqtt, IConfiguration config, ILogger<ViessmannConnector> logger)
    {
        _mqtt = mqtt;
        _config = config;
        _logger = logger;
        _http = new HttpClient();
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var clientId = _config["VIESSMANN_CLIENT_ID"];
        var clientSecret = _config["VIESSMANN_CLIENT_SECRET"];

        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
        {
            _logger.LogWarning("Viessmann credentials not configured. Connector disabled.");
            return;
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await EnsureTokenAsync(clientId, clientSecret, ct);
                await PollAndPublishAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error polling Viessmann API");
            }

            await Task.Delay(TimeSpan.FromMinutes(5), ct);
        }
    }

    private async Task EnsureTokenAsync(string clientId, string clientSecret, CancellationToken ct)
    {
        if (_accessToken != null && DateTime.UtcNow < _tokenExpiry.AddMinutes(-2))
            return;

        var form = _refreshToken != null
            ? new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = _refreshToken,
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret
            }
            : new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret
            };

        var resp = await _http.PostAsync(AuthUrl, new FormUrlEncodedContent(form), ct);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        _accessToken = json.GetProperty("access_token").GetString();
        _refreshToken = json.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
        var expiresIn = json.GetProperty("expires_in").GetInt32();
        _tokenExpiry = DateTime.UtcNow.AddSeconds(expiresIn);

        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
    }

    private async Task PollAndPublishAsync(CancellationToken ct)
    {
        var installationsResp = await _http.GetFromJsonAsync<JsonElement>($"{ApiBase}/equipment/installations", ct);
        if (!installationsResp.TryGetProperty("data", out var installations) || installations.GetArrayLength() == 0)
            return;

        var installationId = installations[0].GetProperty("id").GetInt64();

        var gatewaysResp = await _http.GetFromJsonAsync<JsonElement>(
            $"{ApiBase}/equipment/installations/{installationId}/gateways", ct);

        if (!gatewaysResp.TryGetProperty("data", out var gateways) || gateways.GetArrayLength() == 0)
            return;

        var serial = gateways[0].GetProperty("serial").GetString();

        var featuresUrl = $"{ApiBase}/equipment/installations/{installationId}/gateways/{serial}/devices/0/features";
        var featuresResp = await _http.GetFromJsonAsync<JsonElement>(featuresUrl, ct);

        if (!featuresResp.TryGetProperty("data", out var features))
            return;

        foreach (var feature in features.EnumerateArray())
        {
            if (!feature.TryGetProperty("feature", out var featureName))
                continue;

            var name = featureName.GetString() ?? "";

            await TryPublishFeature(name, feature, ct);
        }

        _logger.LogInformation("Viessmann: published metrics at {Time}", DateTime.UtcNow);
    }

    private async Task TryPublishFeature(string name, JsonElement feature, CancellationToken ct)
    {
        if (!feature.TryGetProperty("properties", out var props))
            return;

        var publish = async (string topic, string propName) =>
        {
            if (props.TryGetProperty(propName, out var prop) && prop.TryGetProperty("value", out var val))
                await _mqtt.PublishAsync(topic, val.ToString()!, ct);
        };

        switch (name)
        {
            case "heating.circuits.0.sensors.temperature.supply":
                await publish(MqttTopics.PdcTempSupply, "value");
                break;
            case "heating.circuits.0.sensors.temperature.return":
                await publish(MqttTopics.PdcTempReturn, "value");
                break;
            case "heating.sensors.temperature.outside":
                await publish(MqttTopics.PdcOutdoorTemp, "value");
                break;
            case "heating.dhw.sensors.temperature.hotWaterStorage":
                await publish(MqttTopics.PdcDhwTemperature, "value");
                break;
            case "heating.compressors.0":
                if (props.TryGetProperty("active", out var active))
                    await _mqtt.PublishAsync(MqttTopics.PdcMode, active.GetProperty("value").GetBoolean() ? "heating" : "standby", ct);
                break;
        }
    }
}

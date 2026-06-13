using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CasaTimo.Core.Interfaces;
using CasaTimo.Core.Models;

namespace CasaTimo.Workers;

public class HuaweiConnector : BackgroundService
{
    private readonly IMqttService _mqtt;
    private readonly IConfiguration _config;
    private readonly ILogger<HuaweiConnector> _logger;
    private readonly HttpClient _http;
    private string? _xsrfToken;

    private const string BaseUrl = "https://eu5.fusionsolar.huawei.com/thirdData";

    public HuaweiConnector(IMqttService mqtt, IConfiguration config, ILogger<HuaweiConnector> logger)
    {
        _mqtt = mqtt;
        _config = config;
        _logger = logger;

        var handler = new HttpClientHandler { CookieContainer = new CookieContainer() };
        _http = new HttpClient(handler) { BaseAddress = new Uri(BaseUrl) };
        _http.DefaultRequestHeaders.Add("Accept", "application/json");
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var user = _config["HUAWEI_FUSIONSOLAR_USER"];
        var pass = _config["HUAWEI_FUSIONSOLAR_PASS"];

        if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(pass))
        {
            _logger.LogWarning("Huawei FusionSolar credentials not configured. Connector disabled.");
            return;
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await LoginAsync(user, pass, ct);
                await PollAndPublishAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error polling Huawei FusionSolar API");
                _xsrfToken = null;
            }

            await Task.Delay(TimeSpan.FromMinutes(5), ct);
        }
    }

    private async Task LoginAsync(string user, string pass, CancellationToken ct)
    {
        if (_xsrfToken != null)
            return;

        var body = JsonSerializer.Serialize(new { userName = user, systemCode = pass });
        var content = new StringContent(body, Encoding.UTF8, "application/json");
        var resp = await _http.PostAsync("/thirdData/login", content, ct);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        if (!json.GetProperty("success").GetBoolean())
            throw new Exception("Huawei login failed: " + json.GetProperty("failCode"));

        _xsrfToken = resp.Headers.TryGetValues("xsrf-token", out var tokens) ? tokens.First() : "";
        _http.DefaultRequestHeaders.Remove("xsrf-token");
        _http.DefaultRequestHeaders.Add("xsrf-token", _xsrfToken);
        _logger.LogInformation("Huawei FusionSolar: logged in");
    }

    private async Task PollAndPublishAsync(CancellationToken ct)
    {
        var stationCode = _config["HUAWEI_STATION_CODE"] ?? "";

        var body = JsonSerializer.Serialize(new { stationCodes = stationCode });
        var content = new StringContent(body, Encoding.UTF8, "application/json");
        var resp = await _http.PostAsync("/thirdData/getStationRealKpi", content, ct);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        if (!json.GetProperty("success").GetBoolean())
        {
            _xsrfToken = null;
            return;
        }

        var dataList = json.GetProperty("data");
        if (dataList.GetArrayLength() == 0) return;

        var data = dataList[0].GetProperty("dataItemMap");

        await PublishIfPresent(data, "radiation_intensity", MqttTopics.FvPowerProduction, ct);
        await PublishIfPresent(data, "day_power", MqttTopics.FvEnergyToday, ct);
        await PublishIfPresent(data, "real_health_state", null, ct);

        // Battery
        var battBody = JsonSerializer.Serialize(new { sns = new[] { stationCode } });
        var battContent = new StringContent(battBody, Encoding.UTF8, "application/json");
        try
        {
            var battResp = await _http.PostAsync("/thirdData/getDevRealKpi", battContent, ct);
            if (battResp.IsSuccessStatusCode)
            {
                var battJson = await battResp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
                if (battJson.GetProperty("success").GetBoolean() && battJson.GetProperty("data").GetArrayLength() > 0)
                {
                    var battData = battJson.GetProperty("data")[0].GetProperty("dataItemMap");
                    await PublishIfPresent(battData, "battery_soc", MqttTopics.FvBatterySoc, ct);
                    await PublishIfPresent(battData, "ch_discharge_power", MqttTopics.FvBatteryPower, ct);
                    await PublishIfPresent(battData, "grid_exported_power", MqttTopics.FvGridExport, ct);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not fetch battery data");
        }

        _logger.LogInformation("Huawei: published solar metrics at {Time}", DateTime.UtcNow);
    }

    private async Task PublishIfPresent(JsonElement data, string key, string? topic, CancellationToken ct)
    {
        if (topic == null) return;
        if (data.TryGetProperty(key, out var val))
            await _mqtt.PublishAsync(topic, val.ToString()!, ct);
    }
}

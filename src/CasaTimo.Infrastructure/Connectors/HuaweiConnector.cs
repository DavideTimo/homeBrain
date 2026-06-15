using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CasaTimo.Infrastructure.Messaging;
using CasaTimo.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CasaTimo.Infrastructure.Connectors;

/// <summary>
/// Huawei FusionSolar connector.
/// Polls station + device real-time KPIs and publishes on casatimo/fv/* topics.
///
/// Topics published:
///   casatimo/fv/power/production      kW  — inverter AC output
///   casatimo/fv/energy/today          kWh — daily generation
///   casatimo/fv/battery/soc           %   — battery state of charge
///   casatimo/fv/battery/power         kW  — positive=charging, negative=discharging
///   casatimo/fv/battery/status        —   — 0=offline 1=standby 2=running 3=fault
///   casatimo/fv/battery/temperature   °C  — battery temperature
///   casatimo/fv/grid/power            kW  — positive=importing, negative=exporting
///   casatimo/fv/inverter/temperature  °C  — inverter temperature
///   casatimo/fv/inverter/state        —   — inverter state code
/// </summary>
public class HuaweiConnector : IHostedService, IDisposable
{
    // Huawei FusionSolar devTypeId constants
    private const int DevTypeInverter            = 1;
    private const int DevTypeResidentialInverter = 38;
    private const int DevTypeBattery             = 39;
    private const int DevTypeGridMeter           = 10;

    private readonly ILogger<HuaweiConnector> _logger;
    private readonly IMessageBroker _broker;
    private readonly ConnectorStatusReporter _statusReporter;
    private readonly HuaweiOptions _options;
    private readonly HttpClient _http;

    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    // Cached after first discovery
    private string? _stationCode;
    private string? _inverterId;
    private string? _batteryId;
    private string? _gridMeterId;
    private int     _inverterDevType;

    public HuaweiConnector(
        IConfiguration configuration,
        ILogger<HuaweiConnector> logger,
        IHttpClientFactory httpFactory,
        IMessageBroker broker,
        ConnectorStatusReporter statusReporter)
    {
        _logger  = logger;
        _broker  = broker;
        _statusReporter = statusReporter;
        _options = new HuaweiOptions();
        configuration.GetSection("Huawei").Bind(_options);

        // CookieContainer keeps the session cookie between requests
        var handler = new HttpClientHandler { CookieContainer = new CookieContainer() };
        _http = new HttpClient(handler) { BaseAddress = new Uri(_options.BaseUrl.TrimEnd('/') + "/") };
        _http.DefaultRequestHeaders.Add("Accept", "application/json");

        _stationCode = _options.StationCode;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(_options.UserName) || string.IsNullOrEmpty(_options.SystemCode))
        {
            _logger.LogInformation("HuaweiConnector: UserName/SystemCode not configured, skipping");
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
                if (!await EnsureSessionAsync(ct)) goto delay;
                if (!await EnsureDiscoveryAsync(ct)) goto delay;
                await PollAsync(ct);
                await _statusReporter.ReportAsync("huawei", true, null, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "HuaweiConnector poll error");
                await _statusReporter.ReportAsync("huawei", false, ex.Message, ct);
            }

            delay:
            await Task.Delay(TimeSpan.FromSeconds(_options.PollIntervalSeconds), ct).ConfigureAwait(false);
        }
    }

    // ── Session ───────────────────────────────────────────────────────────────

    private bool _sessionOk;

    private async Task<bool> EnsureSessionAsync(CancellationToken ct)
    {
        if (_sessionOk) return true;

        try
        {
            _logger.LogInformation("HuaweiConnector: logging in as {user}", _options.UserName);
            var resp = await _http.PostAsJsonAsync("thirdData/login",
                new { userName = _options.UserName, systemCode = _options.SystemCode }, ct);

            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("HuaweiConnector: login returned {status}", resp.StatusCode);
                return false;
            }

            // Extract XSRF-TOKEN from response and set as default header
            using var doc = await JsonDocument.ParseAsync(
                await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);

            if (doc.RootElement.TryGetProperty("data", out var data) &&
                data.TryGetProperty("xsrf-token", out var tokenEl))
            {
                var token = tokenEl.GetString();
                if (!string.IsNullOrEmpty(token))
                {
                    _http.DefaultRequestHeaders.Remove("XSRF-TOKEN");
                    _http.DefaultRequestHeaders.Add("XSRF-TOKEN", token);
                }
            }

            _sessionOk = true;
            _logger.LogInformation("HuaweiConnector: session established");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "HuaweiConnector: login failed");
            return false;
        }
    }

    private void InvalidateSession()
    {
        _sessionOk = false;
        _inverterId = null;
        _batteryId  = null;
        _gridMeterId = null;
    }

    // ── Discovery ─────────────────────────────────────────────────────────────

    private async Task<bool> EnsureDiscoveryAsync(CancellationToken ct)
    {
        if (_inverterId != null) return true;

        // Discover station if not pre-configured
        if (string.IsNullOrEmpty(_stationCode))
        {
            _stationCode = await DiscoverStationCodeAsync(ct);
            if (string.IsNullOrEmpty(_stationCode)) return false;
        }

        return await DiscoverDevicesAsync(ct);
    }

    private async Task<string?> DiscoverStationCodeAsync(CancellationToken ct)
    {
        try
        {
            var resp = await PostApiAsync("thirdData/getStationList", new { }, ct);
            if (resp == null) return null;

            if (resp.RootElement.TryGetProperty("data", out var data) &&
                data.ValueKind == JsonValueKind.Array)
            {
                foreach (var station in data.EnumerateArray())
                {
                    if (station.TryGetProperty("stationCode", out var code))
                    {
                        var sc = code.GetString();
                        _logger.LogInformation("HuaweiConnector: discovered stationCode={code}", sc);
                        return sc;
                    }
                }
            }
        }
        catch (Exception ex) { _logger.LogError(ex, "HuaweiConnector: station discovery failed"); }
        return null;
    }

    private async Task<bool> DiscoverDevicesAsync(CancellationToken ct)
    {
        try
        {
            var resp = await PostApiAsync("thirdData/getDevList",
                new { stationCodes = _stationCode }, ct);
            if (resp == null) return false;

            if (!resp.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Array)
                return false;

            foreach (var dev in data.EnumerateArray())
            {
                var devId   = GetString(dev, "id") ?? GetString(dev, "devId");
                var typeId  = GetInt(dev, "devTypeId");
                var devName = GetString(dev, "devName") ?? "unknown";

                if (devId == null) continue;

                switch (typeId)
                {
                    case DevTypeInverter:
                    case DevTypeResidentialInverter:
                        _inverterId      = devId;
                        _inverterDevType = typeId;
                        _logger.LogInformation("HuaweiConnector: inverter id={id} type={t} name={n}",
                            devId, typeId, devName);
                        break;
                    case DevTypeBattery:
                        _batteryId = devId;
                        _logger.LogInformation("HuaweiConnector: battery id={id} name={n}", devId, devName);
                        break;
                    case DevTypeGridMeter:
                        _gridMeterId = devId;
                        _logger.LogInformation("HuaweiConnector: grid meter id={id} name={n}", devId, devName);
                        break;
                }
            }

            return _inverterId != null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "HuaweiConnector: device discovery failed");
            return false;
        }
    }

    // ── Polling ───────────────────────────────────────────────────────────────

    private async Task PollAsync(CancellationToken ct)
    {
        await PollStationKpisAsync(ct);
        await PollDeviceKpisAsync(_inverterId!, _inverterDevType, ct);
        if (_batteryId  != null) await PollDeviceKpisAsync(_batteryId,   DevTypeBattery,  ct);
        if (_gridMeterId != null) await PollDeviceKpisAsync(_gridMeterId, DevTypeGridMeter, ct);
    }

    private async Task PollStationKpisAsync(CancellationToken ct)
    {
        var resp = await PostApiAsync("thirdData/getStationRealKpi",
            new { stationCodes = _stationCode }, ct);
        if (resp == null) return;

        var kpis = ExtractDataItemMap(resp, _stationCode);
        if (kpis == null) return;

        await PublishKpiAsync(kpis, "total_current_day_energy", "casatimo/fv/energy/today", ct);
    }

    private async Task PollDeviceKpisAsync(string devId, int devTypeId, CancellationToken ct)
    {
        var resp = await PostApiAsync("thirdData/getDevRealKpi",
            new { devIds = devId, devTypeId }, ct);
        if (resp == null) return;

        var kpis = ExtractDataItemMap(resp, devId);
        if (kpis == null) return;

        switch (devTypeId)
        {
            case DevTypeInverter:
            case DevTypeResidentialInverter:
                await PublishKpiAsync(kpis, "active_power",  "casatimo/fv/power/production",     ct);
                await PublishKpiAsync(kpis, "temperature",   "casatimo/fv/inverter/temperature",  ct);
                await PublishKpiAsync(kpis, "inverter_state","casatimo/fv/inverter/state",        ct);
                // Some firmware includes battery data in inverter KPIs
                await PublishKpiAsync(kpis, "battery_soc",          "casatimo/fv/battery/soc",         ct);
                await PublishKpiAsync(kpis, "ch_discharge_power",   "casatimo/fv/battery/power",       ct);
                await PublishKpiAsync(kpis, "battery_temperature",  "casatimo/fv/battery/temperature", ct);
                break;

            case DevTypeBattery:
                await PublishKpiAsync(kpis, "battery_soc",         "casatimo/fv/battery/soc",         ct);
                await PublishKpiAsync(kpis, "ch_discharge_power",  "casatimo/fv/battery/power",       ct);
                await PublishKpiAsync(kpis, "battery_status",      "casatimo/fv/battery/status",      ct);
                await PublishKpiAsync(kpis, "battery_temperature", "casatimo/fv/battery/temperature", ct);
                break;

            case DevTypeGridMeter:
                await PublishKpiAsync(kpis, "active_power", "casatimo/fv/grid/power", ct);
                break;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<JsonDocument?> PostApiAsync(string path, object body, CancellationToken ct)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync(path, body, ct);
            if (resp.StatusCode == HttpStatusCode.Unauthorized ||
                resp.StatusCode == HttpStatusCode.Forbidden)
            {
                _logger.LogWarning("HuaweiConnector: session expired, will re-login on next poll");
                InvalidateSession();
                return null;
            }

            var content = await resp.Content.ReadAsStringAsync(ct);
            var doc = JsonDocument.Parse(content);

            // FusionSolar wraps errors in success:false
            if (doc.RootElement.TryGetProperty("success", out var success) &&
                success.ValueKind == JsonValueKind.False)
            {
                var msg = doc.RootElement.TryGetProperty("failCode", out var fc) ? fc.ToString() : "unknown";
                _logger.LogWarning("HuaweiConnector: API error on {path}: {msg}", path, msg);

                // failCode 305 = session expired
                if (msg == "305") InvalidateSession();
                return null;
            }

            return doc;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "HuaweiConnector: POST {path} failed", path);
            return null;
        }
    }

    /// <summary>
    /// FusionSolar responses have shape: { "data": [ { "devId"/"stationCode": ..., "dataItemMap": { ... } } ] }
    /// </summary>
    private static Dictionary<string, JsonElement>? ExtractDataItemMap(JsonDocument doc, string? id)
    {
        if (!doc.RootElement.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var item in data.EnumerateArray())
        {
            if (item.TryGetProperty("dataItemMap", out var map))
                return map.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
        }
        return null;
    }

    private async Task PublishKpiAsync(
        Dictionary<string, JsonElement> kpis,
        string kpiKey,
        string topic,
        CancellationToken ct)
    {
        if (!kpis.TryGetValue(kpiKey, out var el)) return;

        string payload;
        if (el.ValueKind == JsonValueKind.Number)
            payload = el.GetDouble().ToString("G6", CultureInfo.InvariantCulture);
        else if (el.ValueKind == JsonValueKind.String)
            payload = el.GetString() ?? string.Empty;
        else
            payload = el.ToString();

        await _broker.PublishAsync(topic, payload, ct);
        _logger.LogInformation("HuaweiConnector: {topic} = {value}", topic, payload);
    }

    private static string? GetString(JsonElement el, string key)
        => el.TryGetProperty(key, out var v) ? v.GetString() : null;

    private static int GetInt(JsonElement el, string key)
        => el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

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
        _http.Dispose();
    }
}

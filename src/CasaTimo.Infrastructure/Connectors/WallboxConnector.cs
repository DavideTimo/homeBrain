using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using CasaTimo.Infrastructure.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CasaTimo.Infrastructure.Connectors;

/// <summary>
/// OCPP 1.6 JSON (OCPP-J) Central System — server WebSocket a cui la wallbox si connette.
///
/// La wallbox (Gewiss GWJ3002A) va configurata con:
///   WebSocket URL: ws://IP_SERVER:9000/ocpp/{chargePointId}
///   Subprotocol:   ocpp1.6
///
/// Topics MQTT pubblicati:
///   casatimo/wallbox/status              Available | Charging | Faulted | Unavailable
///   casatimo/wallbox/power               kW corrente durante la ricarica
///   casatimo/wallbox/session/energy      kWh accumulati nella sessione attiva
///   casatimo/wallbox/session/start       timestamp ISO avvio sessione
///   casatimo/wallbox/session/stop        kWh totali sessione conclusa
/// </summary>
public class WallboxConnector : IHostedService, IDisposable
{
    private readonly ILogger<WallboxConnector> _logger;
    private readonly IMessageBroker _broker;
    private readonly WallboxOptions _options;

    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _serverTask;

    // Stato sessione corrente (una sola wallbox per ora)
    private readonly ConcurrentDictionary<string, ChargeSession> _sessions = new();

    public WallboxConnector(
        IConfiguration configuration,
        ILogger<WallboxConnector> logger,
        IMessageBroker broker)
    {
        _logger  = logger;
        _broker  = broker;
        _options = new WallboxOptions();
        configuration.GetSection("Wallbox").Bind(_options);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _serverTask = RunServerAsync(_cts.Token);
        _logger.LogInformation("WallboxConnector: server OCPP 1.6 in ascolto su porta {port}", _options.Port);
        return Task.CompletedTask;
    }

    // ── HTTP/WebSocket Server ─────────────────────────────────────────────────

    private async Task RunServerAsync(CancellationToken ct)
    {
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://+:{_options.Port}/ocpp/");
        _listener.Start();

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var context = await _listener.GetContextAsync().WaitAsync(ct);

                if (!context.Request.IsWebSocketRequest)
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    continue;
                }

                // Verifica auth opzionale
                if (!string.IsNullOrEmpty(_options.AuthKey))
                {
                    var auth = context.Request.Headers["Authorization"];
                    if (auth != $"Basic {_options.AuthKey}")
                    {
                        context.Response.StatusCode = 401;
                        context.Response.Close();
                        continue;
                    }
                }

                var chargePointId = context.Request.Url?.Segments.LastOrDefault()?.Trim('/') ?? "unknown";
                _ = HandleChargePointAsync(context, chargePointId, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogError(ex, "WallboxConnector: errore server");
                await Task.Delay(2000, ct).ConfigureAwait(false);
            }
        }

        _listener.Stop();
    }

    // ── Gestione singola connessione OCPP ─────────────────────────────────────

    private async Task HandleChargePointAsync(
        HttpListenerContext context, string chargePointId, CancellationToken ct)
    {
        WebSocket? ws = null;
        try
        {
            var wsContext = await context.AcceptWebSocketAsync("ocpp1.6");
            ws = wsContext.WebSocket;
            _logger.LogInformation("WallboxConnector: connesso {id}", chargePointId);

            var buffer = new byte[4096];
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                using var ms = new System.IO.MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    ms.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", ct);
                    break;
                }

                var json = Encoding.UTF8.GetString(ms.ToArray());
                var reply = await ProcessMessageAsync(json, chargePointId, ct);
                if (reply != null)
                {
                    var replyBytes = Encoding.UTF8.GetBytes(reply);
                    await ws.SendAsync(new ArraySegment<byte>(replyBytes),
                        WebSocketMessageType.Text, true, ct);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogError(ex, "WallboxConnector: errore connessione {id}", chargePointId); }
        finally
        {
            ws?.Dispose();
            _sessions.TryRemove(chargePointId, out _);
            _logger.LogInformation("WallboxConnector: disconnesso {id}", chargePointId);
        }
    }

    // ── OCPP 1.6 Message Handling ─────────────────────────────────────────────

    private async Task<string?> ProcessMessageAsync(
        string json, string chargePointId, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var arr = doc.RootElement;
            if (arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() < 3) return null;

            var messageType = arr[0].GetInt32();
            if (messageType != 2) return null; // Solo Call (2), ignoriamo CallResult(3)/Error(4)

            var messageId = arr[1].GetString() ?? "";
            var action    = arr[2].GetString() ?? "";
            var payload   = arr.GetArrayLength() > 3 ? arr[3] : default;

            _logger.LogDebug("WallboxConnector [{id}]: {action}", chargePointId, action);

            return action switch
            {
                "BootNotification"   => await OnBootNotificationAsync(messageId, payload, chargePointId, ct),
                "Heartbeat"          => OnHeartbeat(messageId),
                "StatusNotification" => await OnStatusNotificationAsync(messageId, payload, chargePointId, ct),
                "MeterValues"        => await OnMeterValuesAsync(messageId, payload, chargePointId, ct),
                "StartTransaction"   => await OnStartTransactionAsync(messageId, payload, chargePointId, ct),
                "StopTransaction"    => await OnStopTransactionAsync(messageId, payload, chargePointId, ct),
                "Authorize"          => OnAuthorize(messageId, payload),
                _                    => CallResult(messageId, "{}")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WallboxConnector: errore parsing messaggio");
            return null;
        }
    }

    private async Task<string> OnBootNotificationAsync(
        string messageId, JsonElement payload, string chargePointId, CancellationToken ct)
    {
        var model  = GetStr(payload, "chargePointModel");
        var vendor = GetStr(payload, "chargePointVendor");
        _logger.LogInformation("WallboxConnector: BootNotification da {vendor} {model} [{id}]",
            vendor, model, chargePointId);

        await _broker.PublishAsync("casatimo/wallbox/status", "Available", ct);

        return CallResult(messageId, $@"{{
            ""status"": ""Accepted"",
            ""currentTime"": ""{DateTime.UtcNow:O}"",
            ""interval"": {_options.HeartbeatTimeoutSeconds}
        }}");
    }

    private static string OnHeartbeat(string messageId)
        => CallResult(messageId, $@"{{""currentTime"": ""{DateTime.UtcNow:O}""}}");

    private async Task<string> OnStatusNotificationAsync(
        string messageId, JsonElement payload, string chargePointId, CancellationToken ct)
    {
        var status      = GetStr(payload, "status");
        var errorCode   = GetStr(payload, "errorCode");
        var connectorId = payload.TryGetProperty("connectorId", out var cEl) ? cEl.GetInt32() : 0;

        _logger.LogInformation("WallboxConnector [{id}]: status={status} connector={conn}",
            chargePointId, status, connectorId);

        if (connectorId == 0 || connectorId == 1)
            await _broker.PublishAsync("casatimo/wallbox/status", status, ct);

        if (errorCode is not null and not "NoError")
            _logger.LogWarning("WallboxConnector [{id}]: errorCode={err}", chargePointId, errorCode);

        return CallResult(messageId, "{}");
    }

    private async Task<string> OnMeterValuesAsync(
        string messageId, JsonElement payload, string chargePointId, CancellationToken ct)
    {
        if (!payload.TryGetProperty("meterValue", out var meterValues)) return CallResult(messageId, "{}");

        foreach (var mv in meterValues.EnumerateArray())
        {
            if (!mv.TryGetProperty("sampledValue", out var samples)) continue;

            foreach (var sample in samples.EnumerateArray())
            {
                var measurand = GetStr(sample, "measurand") ?? "Energy.Active.Import.Register";
                var valueStr  = GetStr(sample, "value") ?? "0";
                var unit      = GetStr(sample, "unit") ?? "";

                if (!double.TryParse(valueStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var value))
                    continue;

                switch (measurand)
                {
                    case "Power.Active.Import":
                        // Converte in kW se necessario
                        var kw = unit == "W" ? value / 1000.0 : value;
                        await _broker.PublishAsync("casatimo/wallbox/power",
                            kw.ToString("G4", CultureInfo.InvariantCulture), ct);
                        _logger.LogInformation("WallboxConnector: power = {kw} kW", kw);
                        break;

                    case "Energy.Active.Import.Register":
                    case "Energy.Active.Import.Interval":
                        var session = _sessions.GetOrAdd(chargePointId, _ => new ChargeSession());
                        var kwh = unit == "Wh" ? value / 1000.0 : value;
                        if (session.StartEnergyWh < 0) session.StartEnergyWh = value;
                        session.CurrentEnergyWh = value;
                        var sessionKwh = (session.CurrentEnergyWh - session.StartEnergyWh) / 1000.0;
                        await _broker.PublishAsync("casatimo/wallbox/session/energy",
                            sessionKwh.ToString("G4", CultureInfo.InvariantCulture), ct);
                        break;
                }
            }
        }

        return CallResult(messageId, "{}");
    }

    private async Task<string> OnStartTransactionAsync(
        string messageId, JsonElement payload, string chargePointId, CancellationToken ct)
    {
        var meterStart   = payload.TryGetProperty("meterStart", out var ms) ? ms.GetDouble() : 0;
        var timestamp    = GetStr(payload, "timestamp") ?? DateTime.UtcNow.ToString("O");
        var transactionId = Math.Abs(Random.Shared.Next());

        var session = _sessions.GetOrAdd(chargePointId, _ => new ChargeSession());
        session.StartEnergyWh   = meterStart;
        session.CurrentEnergyWh = meterStart;

        _logger.LogInformation("WallboxConnector [{id}]: StartTransaction meterStart={m} Wh", chargePointId, meterStart);

        await _broker.PublishAsync("casatimo/wallbox/status", "Charging", ct);
        await _broker.PublishAsync("casatimo/wallbox/session/start", timestamp, ct);
        await _broker.PublishAsync("casatimo/wallbox/session/energy", "0", ct);

        return CallResult(messageId, $@"{{
            ""transactionId"": {transactionId},
            ""idTagInfo"": {{""status"": ""Accepted""}}
        }}");
    }

    private async Task<string> OnStopTransactionAsync(
        string messageId, JsonElement payload, string chargePointId, CancellationToken ct)
    {
        var meterStop  = payload.TryGetProperty("meterStop", out var ms) ? ms.GetDouble() : 0;

        _sessions.TryGetValue(chargePointId, out var session);
        var totalKwh = session != null
            ? (meterStop - session.StartEnergyWh) / 1000.0
            : 0;

        _logger.LogInformation("WallboxConnector [{id}]: StopTransaction totalKwh={kwh}", chargePointId, totalKwh);

        _sessions.TryRemove(chargePointId, out _);

        await _broker.PublishAsync("casatimo/wallbox/status", "Available", ct);
        await _broker.PublishAsync("casatimo/wallbox/power", "0", ct);
        await _broker.PublishAsync("casatimo/wallbox/session/stop",
            totalKwh.ToString("G4", CultureInfo.InvariantCulture), ct);

        return CallResult(messageId, @"{""idTagInfo"": {""status"": ""Accepted""}}");
    }

    private static string OnAuthorize(string messageId, JsonElement payload)
        => CallResult(messageId, @"{""idTagInfo"": {""status"": ""Accepted""}}");

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string CallResult(string messageId, string payload)
        => $"[3,\"{messageId}\",{payload}]";

    private static string? GetStr(JsonElement el, string key)
        => el.ValueKind != JsonValueKind.Undefined && el.TryGetProperty(key, out var v)
            ? v.GetString() : null;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        _listener?.Stop();
        if (_serverTask != null)
        {
            try { await _serverTask.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) { }
        }
    }

    public void Dispose() => _cts?.Dispose();

    // ── Inner types ───────────────────────────────────────────────────────────

    private sealed class ChargeSession
    {
        public double StartEnergyWh   { get; set; } = -1;
        public double CurrentEnergyWh { get; set; }
    }
}

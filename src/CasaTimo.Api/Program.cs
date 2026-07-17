using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using CasaTimo.Core.Models;
using CasaTimo.Infrastructure.Data;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// DbContext
builder.Services.AddDbContext<CasaTimoDbContext>(opts =>
    opts.UseSqlite(builder.Configuration.GetConnectionString("CasaTimoDb") ?? "Data Source=casatimo.db")
);

// JWT Authentication — read key lazily inside the options lambda so that
// test factories (WebApplicationFactory) can override configuration before
// the token validation parameters are resolved.
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        var cfg = builder.Configuration;
        var key = cfg["Jwt:Key"]
            ?? throw new InvalidOperationException("Jwt:Key not configured — see .env.example");
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = cfg["Jwt:Issuer"] ?? "casatimo-api",
            ValidAudience = cfg["Jwt:Audience"] ?? "casatimo-clients",
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key))
        };
    });
builder.Services.AddAuthorization();

// CORS: allow configured origins (restrict for production via AllowedOrigins in appsettings)
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(p => p
        .WithOrigins(builder.Configuration.GetSection("AllowedOrigins").Get<string[]>()
                     ?? ["http://localhost:5228", "http://localhost:5000"])
        .AllowAnyHeader()
        .AllowAnyMethod());
});

var app = builder.Build();

// Ensure DB schema exists on first run (no migrations needed)
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<CasaTimoDbContext>();
    db.Database.EnsureCreated();
}

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

// ── Health ──────────────────────────────────────────────────────────────────
app.MapGet("/", () => Results.Ok(new { message = "CasaTimo API is running", version = "0.1.0" }));
app.MapGet("/health", () => Results.Ok(new { status = "Healthy", uptime = DateTime.UtcNow }));

// ── Auth ─────────────────────────────────────────────────────────────────────
app.MapPost("/api/auth/token", (LoginRequest req, IConfiguration config) =>
{
    var adminPwd = config["AdminPassword"] ?? string.Empty;
    if (string.IsNullOrEmpty(adminPwd) || req.Password != adminPwd)
        return Results.Unauthorized();

    var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(config["Jwt:Key"]!));
    var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
    var expiresHours = int.TryParse(config["Jwt:ExpiresHours"], out var h) ? h : 24;

    var token = new JwtSecurityToken(
        issuer: config["Jwt:Issuer"] ?? "casatimo-api",
        audience: config["Jwt:Audience"] ?? "casatimo-clients",
        claims: [new Claim(ClaimTypes.Role, "Admin")],
        expires: DateTime.UtcNow.AddHours(expiresHours),
        signingCredentials: creds
    );

    return Results.Ok(new { token = new JwtSecurityTokenHandler().WriteToken(token) });
});

// ── Sensors ──────────────────────────────────────────────────────────────────

// Ultimo valore per ogni combinazione deviceId/metric
app.MapGet("/api/sensors/live", async (CasaTimoDbContext db) =>
{
    var latestIds = db.SensorReadings
        .GroupBy(r => new { r.DeviceId, r.Metric })
        .Select(g => g.Max(r => r.Id));

    var readings = await db.SensorReadings
        .Where(r => latestIds.Contains(r.Id))
        .OrderBy(r => r.DeviceId).ThenBy(r => r.Metric)
        .ToListAsync();

    return Results.Ok(readings);
});

// Storico con filtri opzionali
app.MapGet("/api/sensors/history", async (
    CasaTimoDbContext db,
    string? deviceId,
    string? metric,
    DateTime? from,
    DateTime? to,
    int? limit) =>
{
    var q = db.SensorReadings.AsQueryable();
    if (deviceId is not null) q = q.Where(r => r.DeviceId == deviceId);
    if (metric   is not null) q = q.Where(r => r.Metric == metric);
    if (from     is not null) q = q.Where(r => r.Timestamp >= from.Value);
    if (to       is not null) q = q.Where(r => r.Timestamp <= to.Value);

    var results = await q
        .OrderByDescending(r => r.Timestamp)
        .Take(limit ?? 500)
        .ToListAsync();

    return Results.Ok(results);
});

// Lista dispositivi e metriche disponibili
app.MapGet("/api/sensors/devices", async (CasaTimoDbContext db) =>
{
    var devices = await db.SensorReadings
        .GroupBy(r => new { r.DeviceId, r.Metric })
        .Select(g => new
        {
            g.Key.DeviceId,
            g.Key.Metric,
            Count    = g.Count(),
            LastSeen = g.Max(r => r.Timestamp)
        })
        .OrderBy(x => x.DeviceId).ThenBy(x => x.Metric)
        .ToListAsync();

    return Results.Ok(devices);
});

// ── Connectors ───────────────────────────────────────────────────────────────
app.MapGet("/api/connectors", async (CasaTimoDbContext db) =>
    Results.Ok(await db.ConnectorConfigs.ToListAsync()));

app.MapGet("/api/connectors/{name}", async (string name, CasaTimoDbContext db) =>
{
    var cfg = await db.ConnectorConfigs.FirstOrDefaultAsync(c => c.ConnectorName == name);
    return cfg is null ? Results.NotFound() : Results.Ok(cfg);
});

app.MapPut("/api/connectors/{name}", async (string name, HttpRequest request, CasaTimoDbContext db) =>
{
    using var sr = new StreamReader(request.Body);
    var body = await sr.ReadToEndAsync();
    if (string.IsNullOrWhiteSpace(body)) return Results.BadRequest("empty body");

    var cfg = await db.ConnectorConfigs.FirstOrDefaultAsync(c => c.ConnectorName == name);
    if (cfg is null)
    {
        cfg = new ConnectorConfig { ConnectorName = name, SettingsJson = body, UpdatedAt = DateTime.UtcNow };
        db.ConnectorConfigs.Add(cfg);
    }
    else
    {
        cfg.SettingsJson = body;
        cfg.UpdatedAt = DateTime.UtcNow;
    }
    await db.SaveChangesAsync();
    return Results.Ok(cfg);
}).RequireAuthorization();

// ── Cameras ──────────────────────────────────────────────────────────────────
static CameraDto ToCameraDto(Camera c) => new(
    c.Id, c.Name, c.Location, c.Host, c.RtspPort, c.RtspPath, c.RtspSubPath,
    c.Username, !string.IsNullOrEmpty(c.Password), c.Enabled, c.CreatedAt, c.UpdatedAt);

app.MapGet("/api/cameras", async (CasaTimoDbContext db) =>
    Results.Ok((await db.Cameras.OrderBy(c => c.Name).ToListAsync()).Select(ToCameraDto)));

app.MapGet("/api/cameras/{id}", async (string id, CasaTimoDbContext db) =>
{
    var cam = await db.Cameras.FindAsync(id);
    return cam is null ? Results.NotFound() : Results.Ok(ToCameraDto(cam));
});

app.MapPost("/api/cameras", async (CameraWriteRequest req, CasaTimoDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.Host))
        return Results.BadRequest("Name e Host sono obbligatori");

    var cam = new Camera
    {
        Name = req.Name,
        Location = string.IsNullOrWhiteSpace(req.Location) ? "interna" : req.Location,
        Host = req.Host,
        RtspPort = req.RtspPort > 0 ? req.RtspPort : 554,
        RtspPath = string.IsNullOrWhiteSpace(req.RtspPath) ? "/h264Preview_01_main" : req.RtspPath,
        RtspSubPath = req.RtspSubPath,
        Username = req.Username,
        Password = req.Password,
        Enabled = req.Enabled,
    };
    db.Cameras.Add(cam);
    await db.SaveChangesAsync();
    return Results.Created($"/api/cameras/{cam.Id}", ToCameraDto(cam));
}).RequireAuthorization();

app.MapPut("/api/cameras/{id}", async (string id, CameraWriteRequest req, CasaTimoDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.Host))
        return Results.BadRequest("Name e Host sono obbligatori");

    var cam = await db.Cameras.FindAsync(id);
    if (cam is null) return Results.NotFound();

    cam.Name = req.Name;
    cam.Location = string.IsNullOrWhiteSpace(req.Location) ? "interna" : req.Location;
    cam.Host = req.Host;
    cam.RtspPort = req.RtspPort > 0 ? req.RtspPort : 554;
    cam.RtspPath = string.IsNullOrWhiteSpace(req.RtspPath) ? "/h264Preview_01_main" : req.RtspPath;
    cam.RtspSubPath = req.RtspSubPath;
    cam.Username = req.Username;
    // Aggiorna la password solo se il client ne invia una nuova, per non
    // costringere il frontend a rimandare quella esistente ad ogni salvataggio
    if (!string.IsNullOrEmpty(req.Password)) cam.Password = req.Password;
    cam.Enabled = req.Enabled;
    cam.UpdatedAt = DateTime.UtcNow;

    await db.SaveChangesAsync();
    return Results.Ok(ToCameraDto(cam));
}).RequireAuthorization();

app.MapDelete("/api/cameras/{id}", async (string id, CasaTimoDbContext db) =>
{
    var cam = await db.Cameras.FindAsync(id);
    if (cam is null) return Results.NotFound();
    db.Cameras.Remove(cam);
    await db.SaveChangesAsync();
    return Results.NoContent();
}).RequireAuthorization();

// Cattura un singolo frame via ffmpeg per validare host/credenziali di una camera,
// senza doverla prima salvare. Richiede ffmpeg installato sull'host che esegue l'API.
app.MapPost("/api/cameras/test", async (CameraTestRequest req) =>
{
    var userInfo = string.IsNullOrEmpty(req.Username)
        ? ""
        : $"{Uri.EscapeDataString(req.Username)}:{Uri.EscapeDataString(req.Password ?? "")}@";
    var rawPath = string.IsNullOrWhiteSpace(req.RtspPath) ? "/" : req.RtspPath;
    var path = rawPath.StartsWith('/') ? rawPath : $"/{rawPath}";
    var rtspUrl = $"rtsp://{userInfo}{req.Host}:{req.RtspPort}{path}";

    var sw = Stopwatch.StartNew();
    Process? proc = null;
    try
    {
        proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                ArgumentList =
                {
                    "-y", "-rtsp_transport", "tcp",
                    "-i", rtspUrl,
                    "-frames:v", "1",
                    "-f", "image2", "-vcodec", "mjpeg",
                    "pipe:1"
                },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            }
        };
        proc.Start();

        var stdout = new MemoryStream();
        var stdoutTask = proc.StandardOutput.BaseStream.CopyToAsync(stdout);
        var stderrTask = proc.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(true); } catch { }
            return Results.Ok(new CameraTestResult(false,
                "Timeout — nessuna risposta dalla telecamera entro 10s", null, sw.ElapsedMilliseconds));
        }

        await stdoutTask;
        var jpegBytes = stdout.ToArray();

        if (proc.ExitCode != 0 || jpegBytes.Length == 0)
        {
            var stderr = await stderrTask;
            var errLine = stderr.Split('\n').LastOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim()
                          ?? "errore sconosciuto";
            return Results.Ok(new CameraTestResult(false, errLine, null, sw.ElapsedMilliseconds));
        }

        return Results.Ok(new CameraTestResult(true, null, Convert.ToBase64String(jpegBytes), sw.ElapsedMilliseconds));
    }
    catch (System.ComponentModel.Win32Exception)
    {
        return Results.Ok(new CameraTestResult(false,
            "ffmpeg non trovato sul sistema — installalo per usare la diagnostica", null, sw.ElapsedMilliseconds));
    }
    finally
    {
        proc?.Dispose();
    }
}).RequireAuthorization();

app.Run();

public record LoginRequest(string Password);

public record CameraDto(
    string Id, string Name, string Location, string Host, int RtspPort,
    string RtspPath, string? RtspSubPath, string? Username, bool HasPassword,
    bool Enabled, DateTime CreatedAt, DateTime UpdatedAt);

public record CameraWriteRequest(
    string Name, string Location, string Host, int RtspPort,
    string RtspPath, string? RtspSubPath, string? Username, string? Password, bool Enabled);

public record CameraTestRequest(
    string Host, int RtspPort, string RtspPath, string? Username, string? Password);

public record CameraTestResult(bool Success, string? Error, string? PreviewBase64, long ElapsedMs);

public partial class Program { } // required for WebApplicationFactory in tests

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

app.Run();

public record LoginRequest(string Password);
public partial class Program { } // required for WebApplicationFactory in tests

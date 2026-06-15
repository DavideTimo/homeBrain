using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using CasaTimo.Infrastructure.Data;
using CasaTimo.Core.Models;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// ── Database ──────────────────────────────────────────────────────────────────
builder.Services.AddDbContext<CasaTimoDbContext>(opts =>
    opts.UseSqlite(builder.Configuration.GetConnectionString("CasaTimoDb") ?? "Data Source=casatimo.db")
);

// ── JWT Authentication ────────────────────────────────────────────────────────
var jwtKey = builder.Configuration["Jwt:Key"]
    ?? throw new InvalidOperationException("Jwt:Key is required. Set it via environment variable or user-secrets.");
var jwtIssuer   = builder.Configuration["Jwt:Issuer"]   ?? "casatimo";
var jwtAudience = builder.Configuration["Jwt:Audience"] ?? "casatimo";

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(opts =>
    {
        opts.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer           = true,
            ValidateAudience         = true,
            ValidateLifetime         = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer              = jwtIssuer,
            ValidAudience            = jwtAudience,
            IssuerSigningKey         = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            ClockSkew                = TimeSpan.FromMinutes(1)
        };
    });

builder.Services.AddAuthorization();

// ── CORS ──────────────────────────────────────────────────────────────────────
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(p =>
    {
        if (builder.Environment.IsDevelopment())
            p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
        else
            p.WithOrigins(builder.Configuration.GetSection("AllowedOrigins").Get<string[]>() ?? [])
             .AllowAnyHeader()
             .AllowAnyMethod();
    });
});

var app = builder.Build();

// ── DB Migration ──────────────────────────────────────────────────────────────
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<CasaTimoDbContext>();
    await db.Database.MigrateAsync();
}

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

// ── Public endpoints ──────────────────────────────────────────────────────────
app.MapGet("/health", () => Results.Ok(new { status = "Healthy", uptime = DateTime.UtcNow }));

app.MapPost("/auth/login", (LoginRequest req, IConfiguration config) =>
{
    var username = config["Auth:Username"] ?? string.Empty;
    var password = config["Auth:Password"] ?? string.Empty;

    if (string.IsNullOrEmpty(username) || req.Username != username || req.Password != password)
        return Results.Unauthorized();

    var expiryDays = config.GetValue<int>("Jwt:ExpiryDays", 30);
    var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(
        config["Jwt:Key"] ?? throw new InvalidOperationException("Jwt:Key missing")));
    var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

    var token = new JwtSecurityToken(
        issuer:   config["Jwt:Issuer"]   ?? "casatimo",
        audience: config["Jwt:Audience"] ?? "casatimo",
        claims:   [new Claim(ClaimTypes.Name, req.Username)],
        expires:  DateTime.UtcNow.AddDays(expiryDays),
        signingCredentials: creds);

    return Results.Ok(new { token = new JwtSecurityTokenHandler().WriteToken(token) });
});

// ── Protected API endpoints ───────────────────────────────────────────────────
var api = app.MapGroup("/api").RequireAuthorization();

api.MapGet("/connectors", async (CasaTimoDbContext db) =>
    Results.Ok(await db.ConnectorConfigs.ToListAsync()));

api.MapGet("/connectors/{name}", async (string name, CasaTimoDbContext db) =>
{
    var cfg = await db.ConnectorConfigs.FirstOrDefaultAsync(c => c.ConnectorName == name);
    return cfg is null ? Results.NotFound() : Results.Ok(cfg);
});

api.MapPut("/connectors/{name}", async (string name, HttpRequest request, CasaTimoDbContext db) =>
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
});

api.MapGet("/devices", async (CasaTimoDbContext db) =>
    Results.Ok(await db.Devices.Where(d => d.IsActive).ToListAsync()));

api.MapGet("/sensors/live", async (CasaTimoDbContext db) =>
{
    var latest = await db.SensorReadings
        .GroupBy(r => new { r.DeviceId, r.Metric })
        .Select(g => g.OrderByDescending(r => r.Timestamp).First())
        .ToListAsync();
    return Results.Ok(latest);
});

api.MapGet("/sensors/history", async (string? deviceId, string? metric, DateTime? from, DateTime? to, CasaTimoDbContext db) =>
{
    var query = db.SensorReadings.AsQueryable();
    if (!string.IsNullOrEmpty(deviceId)) query = query.Where(r => r.DeviceId == deviceId);
    if (!string.IsNullOrEmpty(metric))   query = query.Where(r => r.Metric == metric);
    if (from.HasValue) query = query.Where(r => r.Timestamp >= from.Value);
    if (to.HasValue)   query = query.Where(r => r.Timestamp <= to.Value);
    return Results.Ok(await query.OrderBy(r => r.Timestamp).ToListAsync());
});

api.MapGet("/bills", async (CasaTimoDbContext db) =>
    Results.Ok(await db.Bills.OrderByDescending(b => b.DueDate).ToListAsync()));

api.MapPost("/bills/{id}/paid", async (long id, CasaTimoDbContext db) =>
{
    var bill = await db.Bills.FindAsync(id);
    if (bill is null) return Results.NotFound();
    bill.IsPaid = true;
    await db.SaveChangesAsync();
    return Results.Ok(bill);
});

api.MapGet("/reminders", async (CasaTimoDbContext db) =>
    Results.Ok(await db.Reminders.Where(r => !r.IsSent).OrderBy(r => r.DueDate).ToListAsync()));

api.MapGet("/maintenance", async (CasaTimoDbContext db) =>
    Results.Ok(await db.MaintenanceRecords.OrderByDescending(m => m.Date).ToListAsync()));

api.MapPost("/maintenance", async (MaintenanceRecord record, CasaTimoDbContext db) =>
{
    db.MaintenanceRecords.Add(record);
    await db.SaveChangesAsync();
    return Results.Created($"/api/maintenance/{record.Id}", record);
});

app.Run();

record LoginRequest(string Username, string Password);

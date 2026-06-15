using CasaTimo.Infrastructure.Data;
using CasaTimo.Core.Models;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<CasaTimoDbContext>(opts =>
    opts.UseSqlite(builder.Configuration.GetConnectionString("CasaTimoDb") ?? "Data Source=casatimo.db")
);

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

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<CasaTimoDbContext>();
    await db.Database.MigrateAsync();
}

app.UseCors();

app.MapGet("/health", () => Results.Ok(new { status = "Healthy", uptime = DateTime.UtcNow }));
app.MapGet("/", () => Results.Ok(new { message = "CasaTimo API is running", version = "0.1.0" }));

app.MapGet("/api/connectors", async (CasaTimoDbContext db) =>
    Results.Ok(await db.ConnectorConfigs.ToListAsync()));

app.MapGet("/api/connectors/{name}", async (string name, CasaTimoDbContext db) =>
{
    var cfg = await db.ConnectorConfigs.FirstOrDefaultAsync(c => c.ConnectorName == name);
    return cfg is null ? Results.NotFound() : Results.Ok(cfg);
});

app.MapPut("/api/connectors/{name}", async (string name, HttpRequest request, CasaTimoDbContext db, IConfiguration config) =>
{
    if (!request.Headers.TryGetValue("X-Admin-Password", out var provided))
        return Results.Unauthorized();
    var admin = config["AdminPassword"] ?? string.Empty;
    if (string.IsNullOrEmpty(admin) || provided != admin)
        return Results.Unauthorized();

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

app.MapGet("/api/devices", async (CasaTimoDbContext db) =>
    Results.Ok(await db.Devices.Where(d => d.IsActive).ToListAsync()));

app.MapGet("/api/sensors/live", async (CasaTimoDbContext db) =>
{
    var latest = await db.SensorReadings
        .GroupBy(r => new { r.DeviceId, r.Metric })
        .Select(g => g.OrderByDescending(r => r.Timestamp).First())
        .ToListAsync();
    return Results.Ok(latest);
});

app.MapGet("/api/sensors/history", async (string? deviceId, string? metric, DateTime? from, DateTime? to, CasaTimoDbContext db) =>
{
    var query = db.SensorReadings.AsQueryable();
    if (!string.IsNullOrEmpty(deviceId)) query = query.Where(r => r.DeviceId == deviceId);
    if (!string.IsNullOrEmpty(metric)) query = query.Where(r => r.Metric == metric);
    if (from.HasValue) query = query.Where(r => r.Timestamp >= from.Value);
    if (to.HasValue) query = query.Where(r => r.Timestamp <= to.Value);
    return Results.Ok(await query.OrderBy(r => r.Timestamp).ToListAsync());
});

app.MapGet("/api/bills", async (CasaTimoDbContext db) =>
    Results.Ok(await db.Bills.OrderByDescending(b => b.DueDate).ToListAsync()));

app.MapPost("/api/bills/{id}/paid", async (long id, CasaTimoDbContext db) =>
{
    var bill = await db.Bills.FindAsync(id);
    if (bill is null) return Results.NotFound();
    bill.IsPaid = true;
    await db.SaveChangesAsync();
    return Results.Ok(bill);
});

app.MapGet("/api/reminders", async (CasaTimoDbContext db) =>
    Results.Ok(await db.Reminders.Where(r => !r.IsSent).OrderBy(r => r.DueDate).ToListAsync()));

app.MapGet("/api/maintenance", async (CasaTimoDbContext db) =>
    Results.Ok(await db.MaintenanceRecords.OrderByDescending(m => m.Date).ToListAsync()));

app.MapPost("/api/maintenance", async (MaintenanceRecord record, CasaTimoDbContext db) =>
{
    db.MaintenanceRecords.Add(record);
    await db.SaveChangesAsync();
    return Results.Created($"/api/maintenance/{record.Id}", record);
});

app.Run();

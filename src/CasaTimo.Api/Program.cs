using CasaTimo.Api.Hubs;
using CasaTimo.Infrastructure.Data;
using CasaTimo.Infrastructure.Extensions;
using CasaTimo.Infrastructure.Mqtt;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c => c.SwaggerDoc("v1", new() { Title = "CasaTimo API", Version = "v1" }));
builder.Services.AddSignalR();
builder.Services.AddHostedService<CasaTimo.Api.Hubs.SensorBroadcastService>();
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.AllowAnyHeader().AllowAnyMethod().AllowCredentials()
     .WithOrigins("http://localhost:5001", "https://casa.timo.dev")));

var app = builder.Build();

// Ensure DB is created
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<CasaTimoDbContext>();
    db.Database.EnsureCreated();
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors();
app.MapHub<SensorHub>("/hubs/sensors");

// Health
app.MapGet("/health", () => Results.Ok(new { status = "ok", time = DateTime.UtcNow }))
   .WithName("Health");

// Devices (static list for now)
app.MapGet("/api/devices", () => Results.Ok(new[]
{
    new { Id = "pdc", Name = "Pompa di Calore Viessmann Vitocal 222-S", Type = "HeatPump", Location = "Centrale termica", IsActive = true },
    new { Id = "fv", Name = "Fotovoltaico Huawei + LUNA 2000", Type = "Solar", Location = "Tetto", IsActive = true },
    new { Id = "wallbox", Name = "Gewiss GWJ3002A 7kW", Type = "Wallbox", Location = "Garage", IsActive = true },
    new { Id = "daikin", Name = "Daikin 5MXM 90N Multisplit", Type = "Hvac", Location = "Casa", IsActive = true },
    new { Id = "vmc", Name = "Viessmann Vitovent 100-D", Type = "Ventilation", Location = "Casa", IsActive = true }
})).WithName("GetDevices");

// Sensor readings
app.MapGet("/api/sensors/history", async (
    CasaTimoDbContext db,
    DateTime? from,
    DateTime? to,
    string? deviceId,
    int limit = 1000) =>
{
    var query = db.SensorReadings.AsQueryable();
    if (from.HasValue) query = query.Where(r => r.Timestamp >= from.Value);
    if (to.HasValue) query = query.Where(r => r.Timestamp <= to.Value);
    if (!string.IsNullOrEmpty(deviceId)) query = query.Where(r => r.DeviceId == deviceId);

    var readings = await query
        .OrderByDescending(r => r.Timestamp)
        .Take(limit)
        .ToListAsync();

    return Results.Ok(readings);
}).WithName("GetSensorHistory");

// Bills
app.MapGet("/api/bills", async (CasaTimoDbContext db, int? year, string? type, bool? paid) =>
{
    var query = db.Bills.AsQueryable();
    if (year.HasValue) query = query.Where(b => b.DueDate.Year == year.Value);
    if (!string.IsNullOrEmpty(type) && Enum.TryParse<CasaTimo.Core.Models.BillType>(type, true, out var bt))
        query = query.Where(b => b.Type == bt);
    if (paid.HasValue) query = query.Where(b => b.IsPaid == paid.Value);

    var bills = await query.OrderByDescending(b => b.DueDate).ToListAsync();
    return Results.Ok(bills);
}).WithName("GetBills");

app.MapGet("/api/bills/{id:int}", async (int id, CasaTimoDbContext db) =>
{
    var bill = await db.Bills.Include(b => b.Reminders).FirstOrDefaultAsync(b => b.Id == id);
    return bill is null ? Results.NotFound() : Results.Ok(bill);
}).WithName("GetBill");

app.MapGet("/api/bills/{id:int}/pdf", async (int id, CasaTimoDbContext db) =>
{
    var bill = await db.Bills.FindAsync(id);
    if (bill?.PdfPath == null || !File.Exists(bill.PdfPath)) return Results.NotFound();
    return Results.File(bill.PdfPath, "application/pdf");
}).WithName("GetBillPdf");

app.MapPost("/api/bills/{id:int}/paid", async (int id, CasaTimoDbContext db) =>
{
    var bill = await db.Bills.FindAsync(id);
    if (bill is null) return Results.NotFound();
    bill.IsPaid = true;
    bill.PaidAt = DateTime.UtcNow;
    await db.SaveChangesAsync();
    return Results.Ok(bill);
}).WithName("MarkBillPaid");

// Reminders
app.MapGet("/api/reminders", async (CasaTimoDbContext db) =>
{
    var reminders = await db.Reminders
        .Where(r => !r.IsSent && r.DueDate <= DateTime.UtcNow.AddDays(14))
        .Include(r => r.Bill)
        .OrderBy(r => r.DueDate)
        .ToListAsync();
    return Results.Ok(reminders);
}).WithName("GetReminders");

app.MapPut("/api/reminders/{id:int}/dismiss", async (int id, CasaTimoDbContext db) =>
{
    var reminder = await db.Reminders.FindAsync(id);
    if (reminder is null) return Results.NotFound();
    reminder.IsSent = true;
    reminder.SentAt = DateTime.UtcNow;
    await db.SaveChangesAsync();
    return Results.Ok();
}).WithName("DismissReminder");

// Maintenance
app.MapGet("/api/maintenance", async (CasaTimoDbContext db) =>
{
    var records = await db.MaintenanceRecords.OrderByDescending(m => m.Date).ToListAsync();
    return Results.Ok(records);
}).WithName("GetMaintenance");

app.MapPost("/api/maintenance", async (CasaTimo.Core.Models.MaintenanceRecord record, CasaTimoDbContext db) =>
{
    db.MaintenanceRecords.Add(record);
    await db.SaveChangesAsync();
    return Results.Created($"/api/maintenance/{record.Id}", record);
}).WithName("AddMaintenance");

app.Run();

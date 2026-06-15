using CasaTimo.Infrastructure.Data;
using CasaTimo.Core.Models;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Register DbContext (SQLite file in app folder)
builder.Services.AddDbContext<CasaTimoDbContext>(opts =>
	opts.UseSqlite(builder.Configuration.GetConnectionString("CasaTimoDb") ?? "Data Source=casatimo.db")
);

// Enable CORS for local development (Blazor client)
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
});

var app = builder.Build();

app.UseCors();

app.MapGet("/health", () => Results.Ok(new { status = "Healthy", uptime = DateTime.UtcNow }));
app.MapGet("/", () => Results.Ok(new { message = "CasaTimo API is running", version = "0.1.0" }));

// Simple admin-protected endpoints for connector settings.
// Protect by sending header 'X-Admin-Password' with value configured in appsettings.
app.MapGet("/api/connectors", async (CasaTimoDbContext db) =>
{
	var list = await db.ConnectorConfigs.ToListAsync();
	return Results.Ok(list);
});

app.MapGet("/api/connectors/{name}", async (string name, CasaTimoDbContext db) =>
{
	var cfg = await db.ConnectorConfigs.FirstOrDefaultAsync(c => c.ConnectorName == name);
	if (cfg == null) return Results.NotFound();
	return Results.Ok(cfg);
});

app.MapPut("/api/connectors/{name}", async (string name, HttpRequest request, CasaTimoDbContext db, IConfiguration config) =>
{
	// basic admin check
	if (!request.Headers.TryGetValue("X-Admin-Password", out var provided))
		return Results.Unauthorized();
	var admin = config["AdminPassword"] ?? string.Empty;
	if (string.IsNullOrEmpty(admin) || provided != admin)
		return Results.Unauthorized();

	using var sr = new StreamReader(request.Body);
	var body = await sr.ReadToEndAsync();
	if (string.IsNullOrWhiteSpace(body)) return Results.BadRequest("empty body");

	var cfg = await db.ConnectorConfigs.FirstOrDefaultAsync(c => c.ConnectorName == name);
	if (cfg == null)
	{
		cfg = new ConnectorConfig { ConnectorName = name, SettingsJson = body, UpdatedAt = DateTime.UtcNow };
		db.ConnectorConfigs.Add(cfg);
	}
	else
	{
		cfg.SettingsJson = body;
		cfg.UpdatedAt = DateTime.UtcNow;
		db.ConnectorConfigs.Update(cfg);
	}
	await db.SaveChangesAsync();
	return Results.Ok(cfg);
});

app.Run();

using CasaTimo.Infrastructure.Data;
using CasaTimo.Infrastructure.Messaging;
using CasaTimo.Infrastructure.Connectors;
using CasaTimo.Infrastructure.Workers;
using CasaTimo.Workers;
using Microsoft.EntityFrameworkCore;

var builder = Host.CreateApplicationBuilder(args);

// DbContext for HistoryRecorder (scoped, created per message via IServiceScopeFactory)
builder.Services.AddDbContext<CasaTimoDbContext>(opts =>
    opts.UseSqlite(builder.Configuration.GetConnectionString("CasaTimoDb") ?? "Data Source=casatimo.db")
);

// Typed HttpClient for the health-check Worker
builder.Services.AddHttpClient<Worker>(c => c.BaseAddress = new Uri("http://localhost:5233"));

// Register MqttClientService as singleton so HistoryRecorder can inject it
builder.Services.AddSingleton<MqttClientService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MqttClientService>());
builder.Services.Configure<MqttOptions>(builder.Configuration.GetSection("Mqtt"));

// HistoryRecorder: subscribes to casatimo/# and persists readings to SQLite
builder.Services.AddHostedService<HistoryRecorder>();

// Viessmann connector (skips gracefully if ApiBaseUrl not configured)
builder.Services.Configure<ViessmannOptions>(builder.Configuration.GetSection("Viessmann"));
builder.Services.AddHttpClient("viessmann");
builder.Services.AddHostedService<ViessmannConnector>();

builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();

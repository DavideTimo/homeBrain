using CasaTimo.Camera;
using CasaTimo.Infrastructure.Data;
using CasaTimo.Infrastructure.Messaging;
using Microsoft.EntityFrameworkCore;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddDbContext<CasaTimoDbContext>(opts =>
    opts.UseSqlite(builder.Configuration.GetConnectionString("CasaTimoDb") ?? "Data Source=casatimo.db")
);

// Si connette al broker MQTT già avviato da CasaTimo.Workers (nessun broker qui).
builder.Services.AddSingleton<MqttClientService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MqttClientService>());
builder.Services.Configure<MqttOptions>(builder.Configuration.GetSection("Mqtt"));

builder.Services.Configure<CameraOptions>(builder.Configuration.GetSection("Camera"));
builder.Services.AddHostedService<CameraSurveillanceWorker>();

var host = builder.Build();

using (var scope = host.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<CasaTimoDbContext>();
    db.Database.EnsureCreated();
}

host.Run();

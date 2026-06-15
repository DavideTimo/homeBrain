using CasaTimo.Workers;
using CasaTimo.Infrastructure.Messaging;
using CasaTimo.Infrastructure.Connectors;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddHttpClient<Worker>(c =>
    c.BaseAddress = new Uri(builder.Configuration["ApiBaseUrl"] ?? "http://localhost:5233"));

// MqttClientService registered as both IHostedService and IMessageBroker
builder.Services.AddSingleton<MqttClientService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MqttClientService>());
builder.Services.AddSingleton<IMessageBroker>(sp => sp.GetRequiredService<MqttClientService>());

builder.Services.AddHttpClient("viessmann");
builder.Services.AddHostedService<ViessmannConnector>();

builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();

using CasaTimo.Workers;
using CasaTimo.Infrastructure.Messaging;
using CasaTimo.Infrastructure.Connectors;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddHttpClient<Worker>(c =>
    c.BaseAddress = new Uri(builder.Configuration["ApiBaseUrl"] ?? "http://localhost:5233"));

// MqttClientService: singleton che implementa IMessageBroker
builder.Services.AddSingleton<MqttClientService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MqttClientService>());
builder.Services.AddSingleton<IMessageBroker>(sp => sp.GetRequiredService<MqttClientService>());

// Connettori
builder.Services.AddHttpClient("viessmann");
builder.Services.AddHostedService<ViessmannConnector>();
builder.Services.AddHttpClient("huawei");
builder.Services.AddHostedService<HuaweiConnector>();

builder.Services.AddHostedService<Worker>();
builder.Services.AddHostedService<HistoryRecorder>();

var host = builder.Build();
host.Run();

using CasaTimo.Workers;
using Microsoft.Extensions.DependencyInjection;
using CasaTimo.Infrastructure.Messaging;
using CasaTimo.Infrastructure.Connectors;

var builder = Host.CreateApplicationBuilder(args);

// Register a typed HttpClient for the Worker with the API base address
builder.Services.AddHttpClient<Worker>(c => c.BaseAddress = new Uri("http://localhost:5233"));

// Configure MQTT options and register the managed MQTT client as a hosted service
builder.Services.Configure<MqttOptions>(builder.Configuration.GetSection("Mqtt"));
builder.Services.AddHostedService<MqttClientService>();

// Viessmann connector (scaffold). Configure section 'Viessmann' in appsettings
builder.Services.AddHttpClient("viessmann");
builder.Services.AddHostedService<ViessmannConnector>();

builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();

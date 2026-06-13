using CasaTimo.Infrastructure.Extensions;
using CasaTimo.Workers;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddHostedService<ViessmannConnector>();
builder.Services.AddHostedService<HuaweiConnector>();
builder.Services.AddHostedService<HistoryRecorder>();
builder.Services.AddHostedService<BillWatcher>();

var host = builder.Build();
host.Run();

using CasaTimo.Web.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<CasaTimo.Web.App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

var apiBase = builder.Configuration["ApiBase"] ?? "http://localhost:5000";

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(apiBase) });
builder.Services.AddScoped<SensorService>();
builder.Services.AddScoped<BillService>();
builder.Services.AddScoped<ReminderService>();

await builder.Build().RunAsync();

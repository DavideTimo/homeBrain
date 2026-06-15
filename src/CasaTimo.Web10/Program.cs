using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using CasaTimo.Web10;
using CasaTimo.Web10.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

var apiBase = builder.Configuration["ApiBaseUrl"] ?? "http://localhost:5233";

builder.Services.AddHttpClient("api", client =>
{
    client.BaseAddress = new Uri(apiBase);
});

builder.Services.AddHttpClient<ApiClient>(client =>
{
    client.BaseAddress = new Uri(apiBase);
});

builder.Services.AddScoped<ApiClient>();

await builder.Build().RunAsync();

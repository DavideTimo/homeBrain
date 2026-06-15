using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using CasaTimo.Web10.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<CasaTimo.Web10.App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

var apiBase = builder.Configuration["ApiBaseUrl"] ?? "http://localhost:5233";

builder.Services.AddHttpClient("api", client =>
{
    client.BaseAddress = new Uri(apiBase);
});

// Auth
builder.Services.AddScoped<JwtAuthStateProvider>();
builder.Services.AddScoped<AuthenticationStateProvider>(sp => sp.GetRequiredService<JwtAuthStateProvider>());
builder.Services.AddScoped<AuthService>();
builder.Services.AddAuthorizationCore();

// API client
builder.Services.AddScoped<ApiClient>();

await builder.Build().RunAsync();

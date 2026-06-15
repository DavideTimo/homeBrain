using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.JSInterop;

namespace CasaTimo.Web10.Services;

public class AuthService
{
    private const string TokenKey = "casatimo_token";

    private readonly HttpClient _http;
    private readonly IJSRuntime _js;
    private readonly JwtAuthStateProvider _authStateProvider;

    public AuthService(IHttpClientFactory factory, IJSRuntime js, JwtAuthStateProvider authStateProvider)
    {
        _http = factory.CreateClient("api");
        _js = js;
        _authStateProvider = authStateProvider;
    }

    public async Task<bool> LoginAsync(string username, string password)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync("/auth/login", new { username, password });
            if (!resp.IsSuccessStatusCode) return false;

            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var token = doc.RootElement.GetProperty("token").GetString();
            if (string.IsNullOrEmpty(token)) return false;

            await _js.InvokeVoidAsync("localStorage.setItem", TokenKey, token);
            _authStateProvider.NotifyUserAuthenticated(token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task LogoutAsync()
    {
        await _js.InvokeVoidAsync("localStorage.removeItem", TokenKey);
        _authStateProvider.NotifyUserLoggedOut();
    }

    public async Task<string?> GetTokenAsync()
        => await _js.InvokeAsync<string?>("localStorage.getItem", TokenKey);
}

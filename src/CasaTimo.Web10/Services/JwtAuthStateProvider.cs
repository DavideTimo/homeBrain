using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;

namespace CasaTimo.Web10.Services;

public class JwtAuthStateProvider : AuthenticationStateProvider
{
    private const string TokenKey = "casatimo_token";
    private readonly IJSRuntime _js;
    private static readonly AuthenticationState Anonymous = new(new ClaimsPrincipal(new ClaimsIdentity()));

    public JwtAuthStateProvider(IJSRuntime js)
    {
        _js = js;
    }

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        try
        {
            var token = await _js.InvokeAsync<string?>("localStorage.getItem", TokenKey);
            if (string.IsNullOrWhiteSpace(token)) return Anonymous;

            var claims = ParseClaimsFromJwt(token);
            var identity = new ClaimsIdentity(claims, "jwt");
            return new AuthenticationState(new ClaimsPrincipal(identity));
        }
        catch
        {
            return Anonymous;
        }
    }

    public void NotifyUserAuthenticated(string token)
    {
        var claims = ParseClaimsFromJwt(token);
        var identity = new ClaimsIdentity(claims, "jwt");
        var user = new ClaimsPrincipal(identity);
        NotifyAuthenticationStateChanged(Task.FromResult(new AuthenticationState(user)));
    }

    public void NotifyUserLoggedOut()
        => NotifyAuthenticationStateChanged(Task.FromResult(Anonymous));

    private static IEnumerable<Claim> ParseClaimsFromJwt(string token)
    {
        var parts = token.Split('.');
        if (parts.Length != 3) return [];

        var payload = parts[1];
        // Pad base64url
        payload = payload.Replace('-', '+').Replace('_', '/');
        payload = (payload.Length % 4) switch
        {
            2 => payload + "==",
            3 => payload + "=",
            _ => payload
        };

        try
        {
            var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(payload));
            using var doc = JsonDocument.Parse(json);
            var claims = new List<Claim>();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                var value = prop.Value.ValueKind == JsonValueKind.String
                    ? prop.Value.GetString() ?? string.Empty
                    : prop.Value.ToString();
                claims.Add(new Claim(prop.Name, value));
            }
            return claims;
        }
        catch
        {
            return [];
        }
    }
}

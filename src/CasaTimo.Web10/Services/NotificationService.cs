using Microsoft.JSInterop;
using System.Net.Http.Json;

namespace CasaTimo.Web10.Services;

public class NotificationService
{
    private readonly IJSRuntime _js;
    private readonly IHttpClientFactory _factory;
    private readonly AuthService _auth;

    public NotificationService(IJSRuntime js, IHttpClientFactory factory, AuthService auth)
    {
        _js      = js;
        _factory = factory;
        _auth    = auth;
    }

    public async Task<bool> IsSupportedAsync()
        => await _js.InvokeAsync<bool>("eval", "'Notification' in window && 'serviceWorker' in navigator && 'PushManager' in window");

    public async Task<bool> IsSubscribedAsync()
    {
        try { return await _js.InvokeAsync<bool>("isPushSubscribed"); }
        catch { return false; }
    }

    public async Task<string> GetPermissionAsync()
        => await _js.InvokeAsync<string>("eval", "Notification.permission");

    public async Task<bool> SubscribeAsync()
    {
        try
        {
            var http = _factory.CreateClient("api");
            var keyResp = await http.GetFromJsonAsync<VapidKeyResponse>("/api/push/vapid-public-key");
            if (keyResp == null || string.IsNullOrEmpty(keyResp.PublicKey)) return false;

            var sub = await _js.InvokeAsync<PushSubscriptionJs?>("subscribePush", keyResp.PublicKey);
            if (sub == null) return false;

            var token = await _auth.GetTokenAsync();
            http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token ?? string.Empty);

            var resp = await http.PostAsJsonAsync("/api/push/subscribe",
                new { endpoint = sub.Endpoint, p256dh = sub.P256dh, auth = sub.Auth });
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public async Task UnsubscribeAsync()
    {
        try
        {
            var endpoint = await _js.InvokeAsync<string?>("getSubscriptionEndpoint");
            if (string.IsNullOrEmpty(endpoint)) return;

            await _js.InvokeVoidAsync("unsubscribePush");

            var http = _factory.CreateClient("api");
            var token = await _auth.GetTokenAsync();
            http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token ?? string.Empty);
            await http.DeleteAsync($"/api/push/subscribe?endpoint={Uri.EscapeDataString(endpoint)}");
        }
        catch { }
    }

    private record VapidKeyResponse(string PublicKey);
    private record PushSubscriptionJs(string Endpoint, string? P256dh, string? Auth);
}

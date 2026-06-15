using System.Net.Http.Json;
using CasaTimo.Web10.Models;

namespace CasaTimo.Web10.Services;

public class ApiClient
{
    private readonly HttpClient _http;
    private readonly AuthService _auth;

    public ApiClient(IHttpClientFactory factory, AuthService auth)
    {
        _http = factory.CreateClient("api");
        _auth = auth;
    }

    private async Task SetAuthHeaderAsync()
    {
        var token = await _auth.GetTokenAsync();
        _http.DefaultRequestHeaders.Authorization = string.IsNullOrEmpty(token)
            ? null
            : new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
    }

    public async Task<List<SensorReadingDto>?> GetSensorLiveAsync()
    {
        await SetAuthHeaderAsync();
        return await _http.GetFromJsonAsync<List<SensorReadingDto>>("/api/sensors/live");
    }

    public async Task<List<DeviceDto>?> GetDevicesAsync()
    {
        await SetAuthHeaderAsync();
        return await _http.GetFromJsonAsync<List<DeviceDto>>("/api/devices");
    }

    public async Task<List<BillDto>?> GetBillsAsync()
    {
        await SetAuthHeaderAsync();
        return await _http.GetFromJsonAsync<List<BillDto>>("/api/bills");
    }

    public async Task<List<ReminderDto>?> GetRemindersAsync()
    {
        await SetAuthHeaderAsync();
        return await _http.GetFromJsonAsync<List<ReminderDto>>("/api/reminders");
    }

    public async Task<List<MaintenanceRecordDto>?> GetMaintenanceAsync()
    {
        await SetAuthHeaderAsync();
        return await _http.GetFromJsonAsync<List<MaintenanceRecordDto>>("/api/maintenance");
    }

    public async Task MarkBillPaidAsync(long id)
    {
        await SetAuthHeaderAsync();
        var resp = await _http.PostAsync($"/api/bills/{id}/paid", null);
        resp.EnsureSuccessStatusCode();
    }

    public async Task AddMaintenanceAsync(MaintenanceRecordDto record)
    {
        await SetAuthHeaderAsync();
        var resp = await _http.PostAsJsonAsync("/api/maintenance", record);
        resp.EnsureSuccessStatusCode();
    }
}

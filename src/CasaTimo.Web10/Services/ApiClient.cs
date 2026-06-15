using System.Net.Http.Json;
using CasaTimo.Web10.Models;

namespace CasaTimo.Web10.Services;

public class ApiClient
{
    private readonly HttpClient _http;

    public ApiClient(HttpClient http)
    {
        _http = http;
    }

    public Task<List<SensorReadingDto>?> GetSensorLiveAsync()
        => _http.GetFromJsonAsync<List<SensorReadingDto>>("/api/sensors/live");

    public Task<List<DeviceDto>?> GetDevicesAsync()
        => _http.GetFromJsonAsync<List<DeviceDto>>("/api/devices");

    public Task<List<BillDto>?> GetBillsAsync()
        => _http.GetFromJsonAsync<List<BillDto>>("/api/bills");

    public Task<List<ReminderDto>?> GetRemindersAsync()
        => _http.GetFromJsonAsync<List<ReminderDto>>("/api/reminders");

    public Task<List<MaintenanceRecordDto>?> GetMaintenanceAsync()
        => _http.GetFromJsonAsync<List<MaintenanceRecordDto>>("/api/maintenance");

    public async Task MarkBillPaidAsync(long id)
    {
        var resp = await _http.PostAsync($"/api/bills/{id}/paid", null);
        resp.EnsureSuccessStatusCode();
    }

    public async Task AddMaintenanceAsync(MaintenanceRecordDto record)
    {
        var resp = await _http.PostAsJsonAsync("/api/maintenance", record);
        resp.EnsureSuccessStatusCode();
    }
}

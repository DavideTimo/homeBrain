using System.Net.Http.Json;

namespace CasaTimo.Web.Services;

public record ReminderDto(int Id, string Message, DateTime DueDate);

public class ReminderService
{
    private readonly HttpClient _http;

    public ReminderService(HttpClient http) => _http = http;

    public async Task<List<ReminderDto>> GetActiveRemindersAsync() =>
        await _http.GetFromJsonAsync<List<ReminderDto>>("/api/reminders") ?? [];
}

using System.Net.Http.Json;

namespace CasaTimo.Web.Services;

public class BillService
{
    private readonly HttpClient _http;

    public BillService(HttpClient http) => _http = http;

    public async Task<List<BillDto>> GetBillsAsync(int? year, string? type, bool unpaidOnly)
    {
        var qs = new List<string>();
        if (year.HasValue) qs.Add($"year={year}");
        if (!string.IsNullOrEmpty(type)) qs.Add($"type={type}");
        if (unpaidOnly) qs.Add("paid=false");
        var url = "/api/bills" + (qs.Any() ? "?" + string.Join("&", qs) : "");
        return await _http.GetFromJsonAsync<List<BillDto>>(url) ?? [];
    }

    public async Task MarkPaidAsync(int id) =>
        await _http.PostAsync($"/api/bills/{id}/paid", null);
}

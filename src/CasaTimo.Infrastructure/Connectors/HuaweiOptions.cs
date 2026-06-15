namespace CasaTimo.Infrastructure.Connectors;

public class HuaweiOptions
{
    public string BaseUrl { get; set; } = "https://eu5.fusionsolar.huawei.com";
    public string? UserName { get; set; }
    public string? SystemCode { get; set; }
    public string? StationCode { get; set; }     // opzionale: auto-discover se assente
    public int PollIntervalSeconds { get; set; } = 300;
}

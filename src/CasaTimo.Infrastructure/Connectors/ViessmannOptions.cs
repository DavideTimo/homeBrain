namespace CasaTimo.Infrastructure.Connectors;

public class ViessmannOptions
{
    public string? TokenEndpoint { get; set; }
    public string? ApiBaseUrl { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public int PollIntervalSeconds { get; set; } = 60;
    public string? InstallationId { get; set; }
    public string? GatewaySerial { get; set; }
}

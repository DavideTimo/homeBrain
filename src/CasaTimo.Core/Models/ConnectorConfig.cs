using System;

namespace CasaTimo.Core.Models;

public class ConnectorConfig
{
    public int Id { get; set; }
    public string ConnectorName { get; set; } = string.Empty;
    // JSON blob with configuration for the connector (store secrets securely in production)
    public string SettingsJson { get; set; } = string.Empty;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastPollAt { get; set; }
    public bool? IsHealthy { get; set; }
    public string? LastError { get; set; }
}

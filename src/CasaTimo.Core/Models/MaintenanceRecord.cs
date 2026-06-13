namespace CasaTimo.Core.Models;

public class MaintenanceRecord
{
    public int Id { get; set; }
    public string DeviceId { get; set; } = "";
    public string Description { get; set; } = "";
    public DateTime Date { get; set; }
    public decimal? Cost { get; set; }
    public DateTime? NextDueDate { get; set; }
    public string? Notes { get; set; }
}

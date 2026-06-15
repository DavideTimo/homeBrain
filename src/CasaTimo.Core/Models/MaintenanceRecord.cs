using System;
using System.ComponentModel.DataAnnotations;

namespace CasaTimo.Core.Models;

public class MaintenanceRecord
{
    [Key]
    public long Id { get; set; }
    public string DeviceId { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTime Date { get; set; }
    public decimal? Cost { get; set; }
    public DateTime? NextDueDate { get; set; }
}

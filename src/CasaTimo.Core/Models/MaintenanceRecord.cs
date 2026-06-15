using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CasaTimo.Core.Models;

public class MaintenanceRecord
{
    [Key]
    public long Id { get; set; }

    public string DeviceId { get; set; } = string.Empty;
    [ForeignKey(nameof(DeviceId))]
    public Device? Device { get; set; }

    public string? Description { get; set; }
    public DateTime Date { get; set; }
    public decimal? Cost { get; set; }
    public DateTime? NextDueDate { get; set; }
}

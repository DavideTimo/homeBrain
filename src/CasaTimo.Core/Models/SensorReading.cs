using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CasaTimo.Core.Models;

public class SensorReading
{
    [Key]
    public long Id { get; set; }

    [Required]
    public string DeviceId { get; set; } = string.Empty;
    [ForeignKey(nameof(DeviceId))]
    public Device? Device { get; set; }

    [Required]
    public string Metric { get; set; } = string.Empty;
    public double Value { get; set; }
    public string? Unit { get; set; }
    public DateTime Timestamp { get; set; }
}

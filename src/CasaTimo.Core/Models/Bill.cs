using System;
using System.ComponentModel.DataAnnotations;

namespace CasaTimo.Core.Models;

public enum BillType { Electricity, Water, Tari, Maintenance, Other }

public class Bill
{
    [Key]
    public long Id { get; set; }
    public BillType Type { get; set; }
    [Required]
    public string Issuer { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public DateTime DueDate { get; set; }
    public DateTime? PeriodFrom { get; set; }
    public DateTime? PeriodTo { get; set; }
    public string? PdfPath { get; set; }
    public string? EmailId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public bool IsPaid { get; set; }
}

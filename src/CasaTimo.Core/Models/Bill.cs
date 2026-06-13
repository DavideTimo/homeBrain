namespace CasaTimo.Core.Models;

public class Bill
{
    public int Id { get; set; }
    public BillType Type { get; set; }
    public string Issuer { get; set; } = "";
    public decimal Amount { get; set; }
    public DateTime DueDate { get; set; }
    public DateTime? PeriodFrom { get; set; }
    public DateTime? PeriodTo { get; set; }
    public string? PdfPath { get; set; }
    public string? EmailId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public bool IsPaid { get; set; }
    public DateTime? PaidAt { get; set; }
    public double? ConsumptionKwh { get; set; }
    public List<Reminder> Reminders { get; set; } = [];
}

public enum BillType
{
    Electricity,
    Water,
    Tari,
    Gas,
    Maintenance,
    Other
}

namespace CasaTimo.Web.Services;

public enum BillType { Electricity, Water, Tari, Gas, Maintenance, Other }

public record BillDto(int Id, BillType Type, string Issuer, decimal Amount, DateTime DueDate,
    DateTime? PeriodFrom, DateTime? PeriodTo, string? PdfPath, bool IsPaid);

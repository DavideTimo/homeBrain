namespace CasaTimo.Web10.Models;

public record SensorReadingDto(long Id, string DeviceId, string Metric, double Value, string? Unit, DateTime Timestamp);
public record DeviceDto(string Id, string Name, string? Type, string? Location, bool IsActive);
public record BillDto(long Id, int Type, string Issuer, decimal Amount, DateTime DueDate, DateTime? PeriodFrom, DateTime? PeriodTo, string? PdfPath, bool IsPaid, DateTime CreatedAt);
public record ReminderDto(long Id, long BillId, DateTime DueDate, int DaysBefore, bool IsSent, string? Message);
public record MaintenanceRecordDto(long Id, string DeviceId, string? Description, DateTime Date, decimal? Cost, DateTime? NextDueDate);
public record ConnectorConfigDto(int Id, string ConnectorName, string SettingsJson, DateTime UpdatedAt);

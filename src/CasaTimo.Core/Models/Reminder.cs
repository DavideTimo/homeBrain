namespace CasaTimo.Core.Models;

public class Reminder
{
    public int Id { get; set; }
    public int BillId { get; set; }
    public Bill? Bill { get; set; }
    public DateTime DueDate { get; set; }
    public int DaysBefore { get; set; }
    public bool IsSent { get; set; }
    public DateTime? SentAt { get; set; }
    public string Message { get; set; } = "";
}

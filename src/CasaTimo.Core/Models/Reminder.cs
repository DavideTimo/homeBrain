using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CasaTimo.Core.Models;

public class Reminder
{
    [Key]
    public long Id { get; set; }

    public long BillId { get; set; }
    [ForeignKey(nameof(BillId))]
    public Bill? Bill { get; set; }

    public DateTime DueDate { get; set; }
    public int DaysBefore { get; set; }
    public bool IsSent { get; set; }
    public string? Message { get; set; }
}

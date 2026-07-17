using System;
using System.ComponentModel.DataAnnotations;

namespace CasaTimo.Core.Models;

/// <summary>Evento rilevato dalla pipeline di videosorveglianza (STEP 13) — motion,
/// persona/veicolo/animale confermato da YOLO. A differenza di SensorReading porta
/// campi propri (confidenza, conteggio, path del JPEG) invece di un singolo valore.</summary>
public class CameraEvent
{
    [Key]
    public long Id { get; set; }
    [Required]
    public string CameraId { get; set; } = string.Empty;
    // "motion" | "person" | "vehicle" | "animal"
    [Required]
    public string EventType { get; set; } = string.Empty;
    public double? Confidence { get; set; }
    public int? Count { get; set; }
    public string? JpegPath { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

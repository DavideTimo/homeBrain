using System;
using System.ComponentModel.DataAnnotations;

namespace CasaTimo.Core.Models;

public class Camera
{
    [Key]
    public string Id { get; set; } = Guid.NewGuid().ToString();
    [Required]
    public string Name { get; set; } = string.Empty;
    // "interna" | "esterna"
    public string Location { get; set; } = "interna";
    [Required]
    public string Host { get; set; } = string.Empty;
    public int RtspPort { get; set; } = 554;
    public string RtspPath { get; set; } = "/h264Preview_01_main";
    // Sub-stream a bassa risoluzione per la pipeline AI, se la camera lo espone separatamente
    public string? RtspSubPath { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

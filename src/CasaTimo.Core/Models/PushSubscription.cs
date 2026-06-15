using System.ComponentModel.DataAnnotations;

namespace CasaTimo.Core.Models;

public class PushSubscription
{
    [Key]
    public int Id { get; set; }
    [Required]
    public string Endpoint { get; set; } = string.Empty;
    public string? P256dh { get; set; }   // subscriber public key
    public string? Auth { get; set; }      // subscriber auth secret
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

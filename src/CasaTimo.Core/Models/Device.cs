using System.ComponentModel.DataAnnotations;

namespace CasaTimo.Core.Models;

public class Device
{
    [Key]
    public string Id { get; set; } = Guid.NewGuid().ToString();
    [Required]
    public string Name { get; set; } = string.Empty;
    public string? Type { get; set; }
    public string? Location { get; set; }
    public bool IsActive { get; set; } = true;
}

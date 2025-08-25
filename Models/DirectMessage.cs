using System.ComponentModel.DataAnnotations;

namespace JaeZoo.Server.Models;

public class DirectMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [Required] public Guid DialogId { get; set; }
    [Required] public Guid SenderId { get; set; }
    [Required, MaxLength(4000)] public string Text { get; set; } = default!;
    public DateTime SentAt { get; set; } = DateTime.UtcNow;
}

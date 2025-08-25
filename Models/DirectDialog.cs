namespace JaeZoo.Server.Models;

public class DirectDialog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid User1Id { get; set; } // всегда «меньший» по Guid
    public Guid User2Id { get; set; } // всегда «больший» по Guid
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

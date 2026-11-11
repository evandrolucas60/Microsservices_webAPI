using Microsoft.EntityFrameworkCore;

namespace Notification.Api.Domain;

public sealed class UserNotification
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? StoreId { get; set; }
    public Guid? ScheduleId { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ReadAt { get; set; }
}

public sealed class NotificationDbContext(DbContextOptions<NotificationDbContext> options) : DbContext(options)
{
    public DbSet<UserNotification> Notifications => Set<UserNotification>();

    protected override void OnModelCreating(ModelBuilder b) => b.Entity<UserNotification>(e =>
    {
        e.ToTable("notifications");
        e.HasKey(x => x.Id);
        e.HasIndex(x => new { x.StoreId, x.CreatedAt });
    });
}

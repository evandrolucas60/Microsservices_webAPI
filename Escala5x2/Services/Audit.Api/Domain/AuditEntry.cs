using Microsoft.EntityFrameworkCore;

namespace Audit.Api.Domain;

/// <summary>Registro somente de inserção: o serviço não expõe edição nem exclusão.</summary>
public sealed class AuditEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string MessageType { get; set; } = string.Empty;
    public Guid? MessageId { get; set; }
    public Guid? CorrelationId { get; set; }
    public Guid? ScheduleId { get; set; }
    public string? Actor { get; set; }
    public string Payload { get; set; } = "{}";
    public DateTime OccurredAt { get; set; }
    public DateTime RecordedAt { get; set; } = DateTime.UtcNow;
}

public sealed class AuditDbContext(DbContextOptions<AuditDbContext> options) : DbContext(options)
{
    public DbSet<AuditEntry> Entries => Set<AuditEntry>();

    protected override void OnModelCreating(ModelBuilder b) => b.Entity<AuditEntry>(e =>
    {
        e.ToTable("audit_entries");
        e.HasKey(x => x.Id);
        e.HasIndex(x => x.MessageId).IsUnique();
        e.HasIndex(x => new { x.MessageType, x.OccurredAt });
        e.HasIndex(x => x.ScheduleId);
        e.Property(x => x.Payload).HasColumnType("jsonb");
    });

    public override int SaveChanges() => throw new NotSupportedException("Use SaveChangesAsync.");

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        if (ChangeTracker.Entries<AuditEntry>().Any(e => e.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Registros de auditoria não podem ser alterados nem apagados.");
        return base.SaveChangesAsync(cancellationToken);
    }
}

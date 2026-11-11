using MassTransit;
using Microsoft.EntityFrameworkCore;
using Scheduling.Api.Domain;

namespace Scheduling.Api.Data;

public sealed class SchedulingDbContext(DbContextOptions<SchedulingDbContext> options) : DbContext(options)
{
    public DbSet<Schedule> Schedules => Set<Schedule>();
    public DbSet<ScheduleAssignment> Assignments => Set<ScheduleAssignment>();
    public DbSet<ScheduleSuggestion> Suggestions => Set<ScheduleSuggestion>();
    public DbSet<ScheduleEditAck> EditAcks => Set<ScheduleEditAck>();

    /// <summary>Escalas aprovadas/publicadas cuja edição de células foi autorizada nesta requisição (PUT cells).</summary>
    private readonly HashSet<Guid> _editAfterApproval = [];

    /// <summary>
    /// Autoriza a edição das células (e da avaliação gravada) de uma escala Aprovada/Publicada nesta requisição: a edição
    /// manual em PUT /cells, que não muda o status e fica auditada. Fora disso essas escalas continuam imutáveis.
    /// </summary>
    public void AllowCellEditAfterApproval(Guid scheduleId) => _editAfterApproval.Add(scheduleId);

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Schedule>(e =>
        {
            e.ToTable("schedules");
            e.HasKey(x => x.Id);
            e.Ignore(x => x.Days);
            e.Ignore(x => x.CanEditCells);
            e.Ignore(x => x.EditRequiresConfirmation);
            e.Ignore(x => x.IsEditable);
            e.Ignore(x => x.CanRevokeApproval);
            e.Ignore(x => x.IsDeletable);
            e.Ignore(x => x.DeleteRequiresAdmin);
            e.Ignore(x => x.CanBePrincipal);
            e.Ignore(x => x.CanCancelGeneration);
            // Cancelamento da geração: estado anterior (gerar na própria escala) e quem cancelou
            e.Property(x => x.GeracaoAnteriorJson).HasColumnType("jsonb");
            e.Property(x => x.GeracaoCanceladaPor).HasMaxLength(200);
            e.Property(x => x.PrincipalPor).HasMaxLength(200);
            e.Property(x => x.PrincipalPedidoPor).HasMaxLength(200);
            // Escalas principais do setor (continuidade e visão do mês)
            e.HasIndex(x => new { x.StoreId, x.SectorId, x.Principal });
            e.Property(x => x.Titulo).HasMaxLength(Schedule.TituloMax);
            e.Property(x => x.Observacao).HasMaxLength(Schedule.ObservacaoMax);
            e.HasIndex(x => new { x.StoreId, x.StartDate, x.Version });
            e.HasIndex(x => new { x.StoreId, x.SectorId });
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.SectorName).HasMaxLength(100);
            e.Property(x => x.GenerationMode).HasMaxLength(20);
            e.Property(x => x.SnapshotJson).HasColumnType("jsonb");
            e.Property(x => x.DeficitsJson).HasColumnType("jsonb");
            e.Property(x => x.DiagnosticoJson).HasColumnType("jsonb");
            e.Property(x => x.SolucoesMotorJson).HasColumnType("jsonb");
            e.Property(x => x.ConformidadeJson).HasColumnType("jsonb");
            e.HasMany(x => x.Assignments).WithOne().HasForeignKey(x => x.ScheduleId);
        });
        b.Entity<ScheduleAssignment>(e =>
        {
            e.ToTable("schedule_assignments");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.ScheduleId, x.EmployeeId, x.Date }).IsUnique();
            e.Property(x => x.Origem).HasConversion<string>().HasMaxLength(10);
            // Linhas antigas recebem "Motor" na migration (BreakStart nulo = intervalo não posicionado)
            e.Property(x => x.BreakOrigem).HasConversion<string>().HasMaxLength(10);
        });
        b.Entity<ScheduleSuggestion>(e =>
        {
            e.ToTable("schedule_suggestions");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.ScheduleId);
            e.Property(x => x.Status).HasMaxLength(20);
            e.Property(x => x.AlteracoesJson).HasColumnType("jsonb");
        });
        b.Entity<ScheduleEditAck>(e =>
        {
            e.ToTable("schedule_edit_acks");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.ScheduleId, x.At });
            e.Property(x => x.By).HasMaxLength(200);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.ViolacoesJson).HasColumnType("jsonb");
            e.HasOne<Schedule>().WithMany().HasForeignKey(x => x.ScheduleId).OnDelete(DeleteBehavior.Cascade);
        });

        // Tabelas do outbox/inbox transacional do MassTransit.
        b.AddInboxStateEntity();
        b.AddOutboxMessageEntity();
        b.AddOutboxStateEntity();
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        GuardImmutability();
        return base.SaveChangesAsync(cancellationToken);
    }

    public override int SaveChanges()
    {
        GuardImmutability();
        return base.SaveChanges();
    }

    private static readonly ScheduleStatus[] Frozen = [ScheduleStatus.Approved, ScheduleStatus.Published, ScheduleStatus.Superseded];

    /// <summary>Campos da escala que a edição manual de uma publicada pode mudar (o quadro e a avaliação dele).</summary>
    private static readonly HashSet<string> EditFields =
    [
        nameof(Schedule.CellsRevision), nameof(Schedule.UpdatedAt), nameof(Schedule.ConformidadePercentual),
        nameof(Schedule.ConformidadeJson), nameof(Schedule.AvaliadaEm)
    ];

    /// <summary>Edição autorizada (<see cref="AllowCellEditAfterApproval"/>) de escala aprovada/publicada que continua no mesmo status.</summary>
    private bool EditAfterApproval(Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<Schedule> schedule, ScheduleStatus original) =>
        _editAfterApproval.Contains(schedule.Entity.Id) && original is ScheduleStatus.Approved or ScheduleStatus.Published
        && schedule.Entity.Status == original && schedule.State == EntityState.Modified;

    /// <summary>Campos da escala principal: mudam em qualquer status (não são o quadro).</summary>
    private static readonly HashSet<string> PrincipalFields =
    [
        nameof(Schedule.Principal), nameof(Schedule.PrincipalDesde), nameof(Schedule.PrincipalPor), nameof(Schedule.PrincipalPedidoPor)
    ];

    /// <summary>
    /// Células só mudam enquanto a escala não foi aprovada (a escala precisa estar carregada no mesmo contexto);
    /// para mexer numa aprovada, a aprovação é desfeita antes (status volta a Gerada, gravado sem mexer nas células).
    /// Escala publicada é registro histórico: só pode passar a "Superseded" quando outra a substitui e ganhar
    /// título/observação. Excluir: nunca a publicada (as células de uma escala excluída saem junto, em qualquer status).
    /// Referência a uma escala excluída (ParentScheduleId) pode ser limpa em qualquer status.
    /// Exceção: a edição manual autorizada na requisição (<see cref="AllowCellEditAfterApproval"/>) muda as células e a
    /// avaliação de uma aprovada/publicada sem mudar o status (auditada pelo Scheduling.Api).
    /// </summary>
    private void GuardImmutability()
    {
        var schedules = ChangeTracker.Entries<Schedule>().ToDictionary(e => e.Entity.Id);
        foreach (var entry in ChangeTracker.Entries<ScheduleAssignment>())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted)) continue;
            if (!schedules.TryGetValue(entry.Entity.ScheduleId, out var parent))
            {
                if (entry.State == EntityState.Added) continue; // inserção sem a escala carregada (não acontece hoje)
                throw new InvalidOperationException("Carregue a escala para alterar as células.");
            }
            if (parent.State == EntityState.Deleted && entry.State == EntityState.Deleted) continue; // exclusão da escala
            var status = parent.State == EntityState.Added
                ? parent.Entity.Status
                : parent.OriginalValues.GetValue<ScheduleStatus>(nameof(Schedule.Status));
            // Edição manual autorizada de aprovada/publicada (o status não muda); substituída nunca
            if (EditAfterApproval(parent, status)) continue;
            if (Frozen.Contains(status))
                throw new InvalidOperationException("Escala aprovada ou publicada não pode ser alterada. Reabra a aprovada para edição ou gere uma nova versão da publicada.");
        }

        var allowed = new HashSet<string>
        {
            nameof(Schedule.Status), nameof(Schedule.SupersededBy), nameof(Schedule.UpdatedAt), nameof(Schedule.Titulo), nameof(Schedule.Observacao)
        };
        allowed.UnionWith(PrincipalFields);
        foreach (var entry in schedules.Values)
        {
            if (entry.State == EntityState.Deleted)
            {
                if (entry.OriginalValues.GetValue<ScheduleStatus>(nameof(Schedule.Status)) == ScheduleStatus.Published)
                    throw new InvalidOperationException("Escala publicada não pode ser excluída: é o registro oficial.");
                continue;
            }
            if (entry.State != EntityState.Modified) continue;

            var original = entry.OriginalValues.GetValue<ScheduleStatus>(nameof(Schedule.Status));
            if (original is not (ScheduleStatus.Published or ScheduleStatus.Superseded)) continue;

            var changed = entry.Properties.Where(p => p.IsModified).Select(p => p.Metadata.Name)
                .Where(n => !(n == nameof(Schedule.ParentScheduleId) && entry.Entity.ParentScheduleId is null)) // pai excluído
                .ToList();
            if (changed.Count == 0 || changed.All(n => n == nameof(Schedule.UpdatedAt))) continue;
            // Edição manual autorizada da publicada: células, revisão e avaliação gravada (status, período e fotografia não)
            if (EditAfterApproval(entry, original) && changed.All(EditFields.Contains)) continue;
            // Marcar/desmarcar a principal não muda o quadro (publicada e substituída também)
            if (entry.Entity.Status == original && changed.All(n => n == nameof(Schedule.UpdatedAt) || PrincipalFields.Contains(n))) continue;
            var metadataOnly = changed.All(n => n is nameof(Schedule.Titulo) or nameof(Schedule.Observacao) or nameof(Schedule.UpdatedAt))
                               && entry.Entity.Status == original;
            if (original == ScheduleStatus.Published && metadataOnly) continue;
            if (original == ScheduleStatus.Superseded || changed.Any(n => !allowed.Contains(n)) || entry.Entity.Status != ScheduleStatus.Superseded)
                throw new InvalidOperationException($"A escala {entry.Entity.Id} já foi publicada e não pode ser alterada.");
        }
    }
}

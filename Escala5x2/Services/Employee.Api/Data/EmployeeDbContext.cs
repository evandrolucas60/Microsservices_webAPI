using Employee.Api.Domain;
using Escala5x2.BuildingBlocks.Contracts.Planning;
using Microsoft.EntityFrameworkCore;

namespace Employee.Api.Data;

public sealed class EmployeeDbContext(DbContextOptions<EmployeeDbContext> options) : DbContext(options)
{
    public DbSet<Domain.Employee> Employees => Set<Domain.Employee>();
    public DbSet<ContractVersion> Contracts => Set<ContractVersion>();
    public DbSet<Unavailability> Unavailabilities => Set<Unavailability>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Domain.Employee>(e =>
        {
            e.ToTable("employees");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.StoreId, x.Registration }).IsUnique();
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Registration).HasMaxLength(30).IsRequired();
            e.Property(x => x.JobTitle).HasMaxLength(100);
            e.Property(x => x.PerfilJornada).HasMaxLength(20).IsRequired().HasDefaultValue(JourneyProfiles.Flexivel);
            e.Property(x => x.DisponibilidadeJson).HasColumnType("jsonb");
            e.Property(x => x.PreferenciasJson).HasColumnType("jsonb");
            e.Property(x => x.CustoHora).HasPrecision(10, 2);
            e.Property(x => x.JornadaAtualizadaPor).HasMaxLength(200);
            e.Property(x => x.LgSituacaoDescricao).HasMaxLength(200);
            e.Property(x => x.LgCentroDeCusto).HasMaxLength(30);
            e.Property(x => x.LgRegistroJson).HasColumnType("jsonb");
            e.Property(x => x.LgOrgaoDescricao).HasMaxLength(100);
            e.Property(x => x.SetoresAlteradosPor).HasMaxLength(200);
            // Mesma pessoa da LG em outra loja (mudança de centro de custo)
            e.HasIndex(x => new { x.LgEmpresaCodigo, x.Registration });
            e.HasMany(x => x.Sectors).WithOne().HasForeignKey(x => x.EmployeeId);
            e.HasMany(x => x.Contracts).WithOne().HasForeignKey(x => x.EmployeeId);
            e.HasMany(x => x.Unavailabilities).WithOne().HasForeignKey(x => x.EmployeeId);
        });
        b.Entity<EmployeeSector>(e =>
        {
            e.ToTable("employee_sectors");
            e.HasKey(x => new { x.EmployeeId, x.SectorId });
        });
        b.Entity<ContractVersion>(e =>
        {
            e.ToTable("contract_versions");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.EmployeeId, x.ValidFrom }).IsUnique();
            e.Property(x => x.SundayRule).HasConversion<string>().HasMaxLength(30);
        });
        b.Entity<Unavailability>(e =>
        {
            e.ToTable("unavailabilities");
            e.HasKey(x => x.Id);
            e.Property(x => x.Type).HasConversion<string>().HasMaxLength(30);
            e.Property(x => x.Note).HasMaxLength(500);
            e.Property(x => x.UpdatedBy).HasMaxLength(200);
            e.Property(x => x.Origem).HasMaxLength(10);
            e.Ignore(x => x.DaLg);
        });
    }
}

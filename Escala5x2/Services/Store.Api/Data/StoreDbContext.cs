using Microsoft.EntityFrameworkCore;
using Store.Api.Domain;

namespace Store.Api.Data;

public sealed class StoreDbContext(DbContextOptions<StoreDbContext> options) : DbContext(options)
{
    public DbSet<Domain.Store> Stores => Set<Domain.Store>();
    public DbSet<Sector> Sectors => Set<Sector>();
    public DbSet<Shift> Shifts => Set<Shift>();
    public DbSet<StaffDemand> Demands => Set<StaffDemand>();
    public DbSet<Holiday> Holidays => Set<Holiday>();
    public DbSet<StoreOperation> Operations => Set<StoreOperation>();
    public DbSet<StoreHours> StoreHours => Set<StoreHours>();
    public DbSet<HolidayStoreHours> HolidayStoreHours => Set<HolidayStoreHours>();
    public DbSet<ManualPeak> ManualPeaks => Set<ManualPeak>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Domain.Store>(e =>
        {
            e.ToTable("stores");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Code).IsUnique();
            e.Property(x => x.Code).HasMaxLength(20).IsRequired();
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Format).HasConversion<string>().HasMaxLength(20);
            // Centro de custo da LG: no máximo uma loja por centro de custo
            e.Property(x => x.LgCentroDeCustoCodigo).HasMaxLength(30);
            e.HasIndex(x => x.LgCentroDeCustoCodigo).IsUnique().HasFilter("\"LgCentroDeCustoCodigo\" IS NOT NULL");
            e.HasMany(x => x.Sectors).WithOne().HasForeignKey(x => x.StoreId);
            e.HasMany(x => x.Shifts).WithOne().HasForeignKey(x => x.StoreId);
            e.HasMany(x => x.Demands).WithOne().HasForeignKey(x => x.StoreId);
            // Cadastro da loja (manual; a sincronização com a LG não toca nestes campos)
            e.Property(x => x.Logradouro).HasMaxLength(200);
            e.Property(x => x.Numero).HasMaxLength(20);
            e.Property(x => x.Bairro).HasMaxLength(100);
            e.Property(x => x.Cep).HasMaxLength(8);
            e.Property(x => x.CodigoIbge).HasMaxLength(7);
            e.Property(x => x.FeriadoFuncionamento).HasConversion<string>().HasMaxLength(10);
            e.Property(x => x.CadastroAtualizadoPor).HasMaxLength(200);
            e.HasMany(x => x.Horarios).WithOne().HasForeignKey(x => x.StoreId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<StoreHours>(e =>
        {
            e.ToTable("store_hours");
            e.HasKey(x => x.Id);
            // Uma linha por dia da semana
            e.HasIndex(x => new { x.StoreId, x.DiaSemana }).IsUnique();
        });
        b.Entity<HolidayStoreHours>(e =>
        {
            e.ToTable("holiday_store_hours");
            e.HasKey(x => x.Id);
            e.Property(x => x.Funcionamento).HasConversion<string>().HasMaxLength(10);
            e.Property(x => x.AtualizadoPor).HasMaxLength(200).IsRequired();
            // Uma exceção por feriado e loja; some com o feriado ou com a loja
            e.HasIndex(x => new { x.HolidayId, x.StoreId }).IsUnique();
            e.HasIndex(x => x.StoreId);
            e.HasOne<Holiday>().WithMany().HasForeignKey(x => x.HolidayId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Domain.Store>().WithMany().HasForeignKey(x => x.StoreId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<ManualPeak>(e =>
        {
            e.ToTable("manual_peaks");
            e.HasKey(x => x.Id);
            e.Ignore(x => x.Dia);
            e.Property(x => x.Descricao).HasMaxLength(150);
            e.Property(x => x.AtualizadoPor).HasMaxLength(200).IsRequired();
            e.HasIndex(x => x.StoreId);
            e.HasOne<Domain.Store>().WithMany().HasForeignKey(x => x.StoreId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<Sector>(e =>
        {
            e.ToTable("sectors");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Origem).HasConversion<string>().HasMaxLength(10);
            e.Property(x => x.LgOrgaoDescricao).HasMaxLength(100);
            // Órgão responsável da LG: no máximo um setor por órgão em cada loja (proteção contra duas sincronizações
            // simultâneas criarem o mesmo setor: a segunda viola o índice e repete lendo o setor já criado)
            e.HasIndex(x => new { x.StoreId, x.LgOrgaoCodigo }).IsUnique().HasFilter("\"LgOrgaoCodigo\" IS NOT NULL");
        });
        b.Entity<Shift>(e =>
        {
            e.ToTable("shifts");
            e.HasKey(x => x.Id);
            e.Property(x => x.Code).HasMaxLength(20);
            e.Ignore(x => x.DurationMinutes);
            e.Ignore(x => x.PaidMinutes);
        });
        b.Entity<StaffDemand>(e => { e.ToTable("staff_demands"); e.HasKey(x => x.Id); });
        b.Entity<Holiday>(e =>
        {
            e.ToTable("holidays");
            e.HasKey(x => x.Id);
            e.Property(x => x.Abrangencia).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Uf).HasMaxLength(2);
            e.Property(x => x.Municipio).HasMaxLength(120);
            e.Property(x => x.LocalChave).HasMaxLength(130).IsRequired();
            e.Property(x => x.Name).HasMaxLength(150).IsRequired();
            e.Property(x => x.AuthorizationRef).HasMaxLength(200);
            e.Property(x => x.CriadoPor).HasMaxLength(200).IsRequired();
            e.Property(x => x.AtualizadoPor).HasMaxLength(200);
            e.Property(x => x.CodigoIbge).HasMaxLength(7);
            e.Property(x => x.Funcionamento).HasConversion<string>().HasMaxLength(10);
            // Listagem por abrangência/local; a duplicidade (mesmo dia, abrangência e local) é conferida na API
            e.HasIndex(x => new { x.Abrangencia, x.LocalChave, x.Date });
        });
        b.Entity<StoreOperation>(e =>
        {
            e.ToTable("store_operations");
            e.HasKey(x => x.Id);
            e.Property(x => x.AtualizadoPor).HasMaxLength(200).IsRequired();
            // Uma operação por loja e uma exceção por setor
            e.HasIndex(x => x.StoreId).IsUnique().HasFilter("\"SectorId\" IS NULL").HasDatabaseName("IX_store_operations_StoreId_loja");
            e.HasIndex(x => x.SectorId).IsUnique().HasFilter("\"SectorId\" IS NOT NULL");
            e.HasOne<Domain.Store>().WithMany().HasForeignKey(x => x.StoreId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}

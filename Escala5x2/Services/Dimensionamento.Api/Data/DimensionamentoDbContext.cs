using Dimensionamento.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Dimensionamento.Api.Data;

public sealed class DimensionamentoDbContext(DbContextOptions<DimensionamentoDbContext> options) : DbContext(options)
{
    public DbSet<PerfilDemanda> Perfis => Set<PerfilDemanda>();
    public DbSet<Cenario> Cenarios => Set<Cenario>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<PerfilDemanda>(e =>
        {
            e.ToTable("perfis_demanda");
            e.HasKey(x => x.Id);
            e.Property(x => x.Nome).HasMaxLength(120).IsRequired();
            e.Property(x => x.Origem).HasMaxLength(20).IsRequired();
            e.Property(x => x.Observacao).HasMaxLength(500);
            e.Property(x => x.CurvaJson).HasColumnType("jsonb").IsRequired();
            e.Property(x => x.FatoresDiaJson).HasColumnType("jsonb");
            e.Property(x => x.CheckoutsJson).HasColumnType("jsonb");
            e.Property(x => x.CriadoPor).HasMaxLength(200).IsRequired();
            e.Property(x => x.AtualizadoPor).HasMaxLength(200);
            e.HasIndex(x => new { x.StoreId, x.SectorId });
        });

        b.Entity<Cenario>(e =>
        {
            e.ToTable("cenarios");
            e.HasKey(x => x.Id);
            e.Property(x => x.Nome).HasMaxLength(120).IsRequired();
            e.Property(x => x.Descricao).HasMaxLength(500);
            e.Property(x => x.ParametrosJson).HasColumnType("jsonb").IsRequired();
            e.Property(x => x.ResumoJson).HasColumnType("jsonb");
            e.Property(x => x.ResultadoJson).HasColumnType("jsonb");
            e.Property(x => x.CriadoPor).HasMaxLength(200).IsRequired();
            e.Property(x => x.AtualizadoPor).HasMaxLength(200);
            e.HasIndex(x => new { x.StoreId, x.SectorId });
            // Perfil excluído: o cenário fica sem perfil (precisa escolher outro para recalcular)
            e.HasOne<PerfilDemanda>().WithMany().HasForeignKey(x => x.PerfilDemandaId).OnDelete(DeleteBehavior.SetNull);
        });
    }
}

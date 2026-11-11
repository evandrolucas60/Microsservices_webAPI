using Compliance.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Compliance.Api.Data;

public sealed class ComplianceDbContext(DbContextOptions<ComplianceDbContext> options) : DbContext(options)
{
    public DbSet<ComplianceRule> Rules => Set<ComplianceRule>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<ComplianceRule>(e =>
        {
            e.ToTable("compliance_rules");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).UseIdentityByDefaultColumn();
            e.Property(x => x.Categoria).HasMaxLength(40).IsRequired();
            e.Property(x => x.Chave).HasMaxLength(80).IsRequired();
            e.Property(x => x.Valor).HasPrecision(12, 2);
            e.Property(x => x.PisoLegal).HasPrecision(12, 2);
            e.Property(x => x.TetoLegal).HasPrecision(12, 2);
            e.Property(x => x.Unidade).HasMaxLength(20).IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Aplicacao).HasConversion<string>().HasMaxLength(20);
            // Chave única por vigência (a sobreposição de vigências é conferida na API)
            e.HasIndex(x => new { x.Chave, x.VigenciaInicio }).IsUnique();
        });
    }
}

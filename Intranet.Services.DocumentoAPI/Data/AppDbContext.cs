using Intranet.Services.DocumentoAPI.Models;
using Microsoft.EntityFrameworkCore;

namespace Intranet.Services.DocumentoAPI.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Documento> documentos { get; set; }

    // Lojas do GC e os arquivos de suas seções
    public DbSet<Loja> lojas { get; set; }
    public DbSet<LojaArquivo> lojaArquivos { get; set; }

    protected override void OnModelCreating(ModelBuilder mb)
    {
        base.OnModelCreating(mb);

        mb.Entity<Loja>(e =>
        {
            e.ToTable("lojas");
            e.HasKey(l => l.LojaId);
            e.Property(l => l.Nome).IsRequired().HasMaxLength(Loja.NomeMaxLength);
            e.Property(l => l.NomeNormalizado).IsRequired().HasMaxLength(Loja.NomeMaxLength);
            e.Property(l => l.CriadoPor).HasMaxLength(450);
            // Nome único sem diferenciar maiúsculas/minúsculas (coluna normalizada em minúsculas)
            e.HasIndex(l => l.NomeNormalizado).IsUnique();
        });

        mb.Entity<LojaArquivo>(e =>
        {
            e.ToTable("lojaArquivos");
            e.HasKey(a => a.LojaArquivoId);
            e.Property(a => a.Secao).IsRequired().HasMaxLength(LojaSecoes.NomeMaxLength);
            e.Property(a => a.Tipo).IsRequired().HasMaxLength(20);
            e.Property(a => a.NomeArquivo).IsRequired().HasMaxLength(LojaArquivo.NomeArquivoMaxLength);
            e.Property(a => a.ContentType).IsRequired().HasMaxLength(100);
            e.Property(a => a.Conteudo).IsRequired();
            e.Property(a => a.Descricao).HasMaxLength(LojaArquivo.DescricaoMaxLength);
            e.Property(a => a.EnviadoPor).HasMaxLength(450);
            e.HasOne(a => a.Loja)
                .WithMany(l => l.Arquivos)
                .HasForeignKey(a => a.LojaId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(a => new { a.LojaId, a.Secao });
        });
    }
}

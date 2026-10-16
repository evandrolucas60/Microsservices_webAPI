using Intranet.Services.GerenciamentoCategoriaAPI.Models;
using Microsoft.EntityFrameworkCore;

namespace Intranet.Services.GerenciamentoCategoriaAPI.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    // Categorias do GC e os seus materiais (fotos e documentos)
    public DbSet<Categoria> categorias { get; set; }
    public DbSet<CategoriaItem> categoriaItens { get; set; }

    protected override void OnModelCreating(ModelBuilder mb)
    {
        base.OnModelCreating(mb);

        mb.Entity<Categoria>(e =>
        {
            e.ToTable("categorias");
            e.HasKey(c => c.CategoriaId);
            e.Property(c => c.Nome).IsRequired().HasMaxLength(Categoria.NomeMaxLength);
            e.Property(c => c.NomeNormalizado).IsRequired().HasMaxLength(Categoria.NomeMaxLength);
            e.Property(c => c.Descricao).HasMaxLength(Categoria.DescricaoMaxLength);
            e.Property(c => c.CriadoPor).HasMaxLength(450);
            // Nome único sem diferenciar maiúsculas/minúsculas (coluna normalizada em minúsculas)
            e.HasIndex(c => c.NomeNormalizado).IsUnique();
            e.HasIndex(c => new { c.Ordem, c.Nome });
        });

        mb.Entity<CategoriaItem>(e =>
        {
            e.ToTable("categoriaItens");
            e.HasKey(i => i.CategoriaItemId);
            e.Property(i => i.Titulo).IsRequired().HasMaxLength(CategoriaItem.TituloMaxLength);
            e.Property(i => i.Descricao).HasMaxLength(CategoriaItem.DescricaoMaxLength);
            e.Property(i => i.Secao).HasMaxLength(GCSecoes.NomeMaxLength);
            e.Property(i => i.Tipo).IsRequired().HasMaxLength(20);
            e.Property(i => i.NomeArquivo).IsRequired().HasMaxLength(CategoriaItem.NomeArquivoMaxLength);
            e.Property(i => i.ContentType).IsRequired().HasMaxLength(100);
            e.Property(i => i.Conteudo).IsRequired();
            e.Property(i => i.EnviadoPor).HasMaxLength(450);
            e.HasOne(i => i.Categoria)
                .WithMany(c => c.Itens)
                .HasForeignKey(i => i.CategoriaId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(i => new { i.CategoriaId, i.EnviadoEm });
            e.HasIndex(i => i.EnviadoEm);
        });
    }
}

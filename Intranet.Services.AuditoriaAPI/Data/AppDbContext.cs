using Intranet.Services.AuditoriaAPI.Models;
using Microsoft.EntityFrameworkCore;

namespace Intranet.Services.AuditoriaAPI.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<ErroLog> Erros { get; set; }
    public DbSet<AcessoLog> Acessos { get; set; }

    protected override void OnModelCreating(ModelBuilder mb)
    {
        base.OnModelCreating(mb);

        mb.Entity<ErroLog>(e =>
        {
            e.ToTable("erros");
            e.HasKey(x => x.Id);
            e.Property(x => x.Servico).HasMaxLength(100).IsRequired();
            e.Property(x => x.UserId).HasMaxLength(100);
            e.Property(x => x.Metodo).HasMaxLength(10).IsRequired();
            e.Property(x => x.Caminho).HasMaxLength(500).IsRequired();
            e.Property(x => x.Mensagem).HasMaxLength(1000).IsRequired();
            e.Property(x => x.CorrelationId).HasMaxLength(100);

            // Filtros do painel: por usuário, por serviço e por período (sempre ordenado por data)
            e.HasIndex(x => new { x.UserId, x.Em });
            e.HasIndex(x => new { x.Servico, x.Em });
            e.HasIndex(x => x.Em);
        });

        mb.Entity<AcessoLog>(e =>
        {
            e.ToTable("acessos");
            e.HasKey(x => x.Id);
            e.Property(x => x.UserId).HasMaxLength(100);
            e.Property(x => x.UserName).HasMaxLength(256);
            e.Property(x => x.Motivo).HasMaxLength(200);
            e.Property(x => x.Ip).HasMaxLength(64);

            e.HasIndex(x => new { x.UserId, x.Em });
            e.HasIndex(x => x.Em);
        });
    }
}

using Intranet.Services.AuthAPI.Models;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Intranet.Services.AuthAPI.Data;

public class AppDbContext : IdentityDbContext<ApplicationUser>
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<ApplicationUser> ApplicationUsers { get; set; }

    /// <summary>Bloqueio/restrições definidos pelo superAdmin (painel de gestão).</summary>
    public DbSet<UserRestriction> UserRestrictions { get; set; }

    protected override void OnModelCreating(ModelBuilder mb)
    {
        base.OnModelCreating(mb);

        mb.Entity<UserRestriction>(e =>
        {
            e.ToTable("UserRestrictions");
            e.HasKey(r => r.UserId);
            e.Property(r => r.Servicos).HasColumnType("text[]");
            e.Property(r => r.MotivoRestricao).HasMaxLength(300);
            e.Property(r => r.MotivoBloqueio).HasMaxLength(300);

            // Exclusão do usuário remove as restrições
            e.HasOne<ApplicationUser>()
                .WithOne()
                .HasForeignKey<UserRestriction>(r => r.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}

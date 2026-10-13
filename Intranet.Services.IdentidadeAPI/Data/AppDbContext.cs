using Intranet.Services.IdentidadeAPI.Models;
using Microsoft.EntityFrameworkCore;

namespace Intranet.Services.IdentidadeAPI.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Address> Addresses { get; set; }
    public DbSet<Member> Members { get; set; }
    public DbSet<UserProfile> Profiles { get; set; }
    public DbSet<Work> Works { get; set; }
    public DbSet<Education> Educations { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Member>()
            .HasOne(m => m.Address)
            .WithOne()
            .HasForeignKey<Address>(a => a.MemberId)
            .OnDelete(DeleteBehavior.Cascade);

        // Um membro por usuário, um perfil e um trabalho por membro.
        // Impede duplicatas criadas por requisições simultâneas (ex.: criação automática do perfil).
        modelBuilder.Entity<Member>()
            .HasIndex(m => m.UserId)
            .IsUnique();

        modelBuilder.Entity<UserProfile>()
            .HasIndex(p => p.MemberId)
            .IsUnique();

        modelBuilder.Entity<Work>()
            .HasIndex(w => w.MemberId)
            .IsUnique();
    }
}


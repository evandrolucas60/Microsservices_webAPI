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
    }
}


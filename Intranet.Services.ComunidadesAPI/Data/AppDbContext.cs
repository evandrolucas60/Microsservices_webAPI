using Intranet.Services.ComunidadesAPI.Models;
using Microsoft.EntityFrameworkCore;

namespace Intranet.Services.ComunidadesAPI.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Group> groups { get; set; }
    public DbSet<GroupMember> groupMembers { get; set; }
    public DbSet<GroupCover> groupCovers { get; set; }
    public DbSet<GroupJoinRequest> groupJoinRequests { get; set; }
    public DbSet<GroupPost> groupPosts { get; set; }
    public DbSet<GroupPostImage> groupPostImages { get; set; }
    public DbSet<GroupPostLike> groupPostLikes { get; set; }
    public DbSet<GroupPostComment> groupPostComments { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Group>(b =>
        {
            b.Property(g => g.Name).HasMaxLength(100).IsRequired();
            b.Property(g => g.Description).HasMaxLength(1000);
            b.Property(g => g.CreatedByUserId).IsRequired();
            b.HasIndex(g => g.Name);
        });

        modelBuilder.Entity<GroupMember>(b =>
        {
            b.Property(m => m.Role).HasMaxLength(10).IsRequired();
            b.HasOne(m => m.Group)
                .WithMany(g => g.Members)
                .HasForeignKey(m => m.GroupId)
                .OnDelete(DeleteBehavior.Cascade);
            // Um registro por membro por grupo
            b.HasIndex(m => new { m.GroupId, m.MemberId }).IsUnique();
            // "Meus grupos"
            b.HasIndex(m => m.MemberId);
        });

        // Capa: 1:1 com o grupo, bytes fora da tabela groups
        modelBuilder.Entity<GroupCover>(b =>
        {
            b.HasKey(c => c.GroupId);
            b.Property(c => c.GroupId).ValueGeneratedNever();
            b.Property(c => c.Data).IsRequired();
            b.Property(c => c.ContentType).HasMaxLength(50).IsRequired();
            b.HasOne(c => c.Group)
                .WithOne(g => g.Cover)
                .HasForeignKey<GroupCover>(c => c.GroupId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<GroupJoinRequest>(b =>
        {
            b.Property(r => r.Status).HasMaxLength(10).IsRequired();
            b.HasOne(r => r.Group)
                .WithMany(g => g.JoinRequests)
                .HasForeignKey(r => r.GroupId)
                .OnDelete(DeleteBehavior.Cascade);
            // No máximo uma solicitação pendente por membro por grupo
            b.HasIndex(r => new { r.GroupId, r.MemberId })
                .IsUnique()
                .HasFilter("\"Status\" = 'pending'");
            // Painel de solicitações pendentes do grupo
            b.HasIndex(r => new { r.GroupId, r.Status, r.CreatedAt });
        });

        modelBuilder.Entity<GroupPost>(b =>
        {
            b.Property(p => p.Text).HasMaxLength(5000).IsRequired();
            b.HasOne(p => p.Group)
                .WithMany(g => g.Posts)
                .HasForeignKey(p => p.GroupId)
                .OnDelete(DeleteBehavior.Cascade);
            // Feed do grupo (mais recentes primeiro)
            b.HasIndex(p => new { p.GroupId, p.CreatedAt });
        });

        modelBuilder.Entity<GroupPostImage>(b =>
        {
            b.HasKey(i => i.GroupPostId);
            b.Property(i => i.GroupPostId).ValueGeneratedNever();
            b.Property(i => i.Data).IsRequired();
            b.Property(i => i.ContentType).HasMaxLength(50).IsRequired();
            b.HasOne(i => i.Post)
                .WithOne(p => p.Image)
                .HasForeignKey<GroupPostImage>(i => i.GroupPostId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<GroupPostLike>(b =>
        {
            b.HasOne(l => l.Post)
                .WithMany(p => p.Likes)
                .HasForeignKey(l => l.GroupPostId)
                .OnDelete(DeleteBehavior.Cascade);
            // Uma curtida por membro por publicação
            b.HasIndex(l => new { l.GroupPostId, l.MemberId }).IsUnique();
        });

        modelBuilder.Entity<GroupPostComment>(b =>
        {
            b.Property(c => c.Text).HasMaxLength(2000).IsRequired();
            b.HasOne(c => c.Post)
                .WithMany(p => p.Comments)
                .HasForeignKey(c => c.GroupPostId)
                .OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(c => new { c.GroupPostId, c.CreatedAt });
        });
    }
}

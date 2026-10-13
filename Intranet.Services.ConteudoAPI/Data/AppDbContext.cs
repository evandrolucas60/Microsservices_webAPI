using Intranet.Services.ConteudoAPI.Models;
using Microsoft.EntityFrameworkCore;

namespace Intranet.Services.ConteudoAPI.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Comment> comments { get; set; }
    public DbSet<Post> posts { get; set; }
    public DbSet<PostLike> postLikes { get; set; }
    public DbSet<CommentLike> commentLikes { get; set; }

    public DbSet<Article> articles { get; set; }
    public DbSet<ArticleComment> articleComments { get; set; }
    public DbSet<ArticleLike> articleLikes { get; set; }
    public DbSet<ArticleImage> articleImages { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Uma curtida por colaborador por post/comentário (evita contagem duplicada)
        modelBuilder.Entity<PostLike>().HasIndex(l => new { l.PostId, l.MemberId }).IsUnique();
        modelBuilder.Entity<CommentLike>().HasIndex(l => new { l.CommentId, l.MemberId }).IsUnique();

        modelBuilder.Entity<ArticleImage>(b =>
        {
            b.Property(i => i.FileName).HasMaxLength(255).IsRequired();
            b.Property(i => i.ContentType).HasMaxLength(100).IsRequired();
            b.Property(i => i.Content).IsRequired();
            b.Property(i => i.UploadedByUserId).IsRequired();
        });

        modelBuilder.Entity<Article>(b =>
        {
            b.Property(a => a.Type).HasMaxLength(20).IsRequired();
            b.Property(a => a.Title).HasMaxLength(200).IsRequired();
            b.Property(a => a.Summary).HasMaxLength(500);
            b.Property(a => a.AuthorUserId).IsRequired();
            b.HasIndex(a => a.CreatedAt);
            b.HasIndex(a => a.Type);
        });

        modelBuilder.Entity<ArticleComment>(b =>
        {
            b.Property(c => c.Text).HasMaxLength(2000).IsRequired();
            b.HasOne(c => c.Article)
                .WithMany(a => a.Comments)
                .HasForeignKey(c => c.ArticleId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ArticleLike>(b =>
        {
            b.HasOne(l => l.Article)
                .WithMany(a => a.Likes)
                .HasForeignKey(l => l.ArticleId)
                .OnDelete(DeleteBehavior.Cascade);
            // Uma curtida por usuário por artigo
            b.HasIndex(l => new { l.ArticleId, l.UserId }).IsUnique();
        });
    }
}

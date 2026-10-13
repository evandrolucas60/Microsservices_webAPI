using Intranet.Services.NotificacaoAPI.Models;
using Microsoft.EntityFrameworkCore;

namespace Intranet.Services.NotificacaoAPI.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Notification> notifications { get; set; }
    public DbSet<EmailNotification> emailNotifications { get; set; }
    public DbSet<PushNotification> pushNotifications { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Uma tabela para a hierarquia (TPH), com a coluna "Channel" identificando o tipo
        modelBuilder.Entity<Notification>(b =>
        {
            b.ToTable("notifications");
            b.HasKey(n => n.NotificationId);
            b.HasDiscriminator<string>("Channel")
                .HasValue<EmailNotification>("email")
                .HasValue<PushNotification>("push");
            b.Property<string>("Channel").HasMaxLength(10);

            b.Property(n => n.Content).HasMaxLength(500).IsRequired();
            b.Property(n => n.Category).HasMaxLength(30).IsRequired();
            b.Property(n => n.Link).HasMaxLength(300);
            b.Property(n => n.LastError).HasMaxLength(1000);

            // Listagem do sininho e worker de envio
            b.HasIndex(n => new { n.RecipientMemberId, n.ReadAt });
            b.HasIndex(n => n.Status);
        });

        modelBuilder.Entity<EmailNotification>(b =>
        {
            b.Property(n => n.Email).HasMaxLength(256).IsRequired();
        });

        modelBuilder.Entity<PushNotification>(b =>
        {
            b.Property(n => n.PhoneNumber).HasMaxLength(30);
        });
    }
}

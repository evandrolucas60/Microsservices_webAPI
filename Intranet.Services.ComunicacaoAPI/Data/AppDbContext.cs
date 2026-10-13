using Intranet.Services.ComunicacaoAPI.Models;
using Microsoft.EntityFrameworkCore;

namespace Intranet.Services.ComunicacaoAPI.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Message> messages { get; set; }
    public DbSet<MessageRecipient> messageRecipients { get; set; }
    public DbSet<MessageMedia> messageMedia { get; set; }

    // Chat (conversas privadas entre dois colaboradores)
    public DbSet<Conversation> conversations { get; set; }
    public DbSet<ChatMessage> chatMessages { get; set; }
    public DbSet<ChatAttachment> chatAttachments { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Conversation>(b =>
        {
            // Uma conversa por dupla (par ordenado)
            b.HasIndex(c => new { c.MemberAId, c.MemberBId }).IsUnique();
            b.HasIndex(c => new { c.MemberBId, c.LastMessageAt });
        });

        modelBuilder.Entity<ChatMessage>(b =>
        {
            b.Property(m => m.Body).HasMaxLength(2000).IsRequired();
            b.HasOne(m => m.Conversation)
                .WithMany(c => c.Messages)
                .HasForeignKey(m => m.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(m => new { m.ConversationId, m.ChatMessageId });
            // Não lidas
            b.HasIndex(m => new { m.ConversationId, m.ReadAt });
        });

        modelBuilder.Entity<ChatAttachment>(b =>
        {
            b.Property(a => a.FileName).HasMaxLength(255).IsRequired();
            b.Property(a => a.ContentType).HasMaxLength(100).IsRequired();
            b.Property(a => a.Content).IsRequired();
            b.HasOne(a => a.Message)
                .WithOne(m => m.Attachment)
                .HasForeignKey<ChatAttachment>(a => a.ChatMessageId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Message>(b =>
        {
            b.Property(m => m.MessageBody).HasMaxLength(5000).IsRequired();
            b.Property(m => m.SenderUserId).IsRequired();
            b.HasIndex(m => new { m.SenderMemberId, m.CreatedAt });
        });

        modelBuilder.Entity<MessageRecipient>(b =>
        {
            b.HasOne(r => r.Message)
                .WithMany(m => m.Recipients)
                .HasForeignKey(r => r.MessageId)
                .OnDelete(DeleteBehavior.Cascade);
            // Um registro por destinatário por mensagem
            b.HasIndex(r => new { r.MessageId, r.MemberId }).IsUnique();
            // Caixa de entrada
            b.HasIndex(r => new { r.MemberId, r.ReadAt });
        });

        modelBuilder.Entity<MessageMedia>(b =>
        {
            b.Property(m => m.FileName).HasMaxLength(255).IsRequired();
            b.Property(m => m.ContentType).HasMaxLength(100).IsRequired();
            b.Property(m => m.Content).IsRequired();
            b.HasOne(m => m.Message)
                .WithMany(m => m.Media)
                .HasForeignKey(m => m.MessageId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}

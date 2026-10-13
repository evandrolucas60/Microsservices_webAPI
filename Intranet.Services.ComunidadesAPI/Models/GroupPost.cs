namespace Intranet.Services.ComunidadesAPI.Models
{
    /// <summary>Publicação (texto simples + imagem opcional) visível apenas aos membros do grupo.</summary>
    public class GroupPost
    {
        public int GroupPostId { get; set; }
        public int GroupId { get; set; }
        public Group Group { get; set; } = null!;

        /// <summary>Autor (sempre vem do token).</summary>
        public Guid MemberId { get; set; }

        /// <summary>Texto simples (sem HTML), até 5000 caracteres. Pode ser vazio se houver imagem.</summary>
        public string Text { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public GroupPostImage? Image { get; set; }
        public ICollection<GroupPostLike> Likes { get; set; } = new List<GroupPostLike>();
        public ICollection<GroupPostComment> Comments { get; set; } = new List<GroupPostComment>();
    }

    /// <summary>Imagem da publicação (bytes no banco, tabela separada para não pesar o feed).</summary>
    public class GroupPostImage
    {
        public int GroupPostId { get; set; }
        public GroupPost Post { get; set; } = null!;

        public byte[] Data { get; set; } = Array.Empty<byte>();
        public string ContentType { get; set; } = string.Empty;
    }

    /// <summary>Curtida: no máximo uma por membro por publicação.</summary>
    public class GroupPostLike
    {
        public int GroupPostLikeId { get; set; }
        public int GroupPostId { get; set; }
        public GroupPost Post { get; set; } = null!;

        public Guid MemberId { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>Comentário (texto simples, até 2000 caracteres).</summary>
    public class GroupPostComment
    {
        public int GroupPostCommentId { get; set; }
        public int GroupPostId { get; set; }
        public GroupPost Post { get; set; } = null!;

        public Guid MemberId { get; set; }
        public string Text { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}

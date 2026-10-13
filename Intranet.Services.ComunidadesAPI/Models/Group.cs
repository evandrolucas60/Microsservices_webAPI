namespace Intranet.Services.ComunidadesAPI.Models
{
    /// <summary>
    /// Group do diagrama: name, description, totalMembers e addMember().
    /// Member joins Group (N:N via <see cref="GroupMember"/>); totalMembers é calculado.
    /// Nome, descrição, capa e total de membros são públicos; o conteúdo (posts, membros,
    /// solicitações) é visível apenas para os membros do grupo.
    /// </summary>
    public class Group
    {
        public int GroupId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }

        public Guid CreatedByMemberId { get; set; }

        /// <summary>Claim "id" de quem criou o grupo.</summary>
        public string CreatedByUserId { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? UpdatedAt { get; set; }

        /// <summary>
        /// Data (UTC) da última troca da capa; null = sem capa. Serve de versão para o cache
        /// do navegador sem carregar os bytes da imagem (que ficam em <see cref="GroupCover"/>).
        /// </summary>
        public DateTime? CoverUpdatedAt { get; set; }

        public ICollection<GroupMember> Members { get; set; } = new List<GroupMember>();
        public ICollection<GroupJoinRequest> JoinRequests { get; set; } = new List<GroupJoinRequest>();
        public ICollection<GroupPost> Posts { get; set; } = new List<GroupPost>();
        public GroupCover? Cover { get; set; }
    }

    public static class GroupRoles
    {
        /// <summary>Dono: todos os poderes, inclusive promover/rebaixar admins e excluir o grupo.</summary>
        public const string Owner = "owner";

        /// <summary>Administrador do grupo: poderes do dono, exceto gerenciar admins e excluir o grupo.</summary>
        public const string Admin = "admin";

        public const string Member = "member";
    }

    /// <summary>Participação de um membro em um grupo (um registro por membro/grupo).</summary>
    public class GroupMember
    {
        public int GroupMemberId { get; set; }
        public int GroupId { get; set; }
        public Group Group { get; set; } = null!;

        public Guid MemberId { get; set; }

        /// <summary>Ver <see cref="GroupRoles"/>.</summary>
        public string Role { get; set; } = GroupRoles.Member;

        public DateTime JoinedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>Imagem de capa do grupo (bytes no banco, tabela separada para não pesar as listagens).</summary>
    public class GroupCover
    {
        public int GroupId { get; set; }
        public Group Group { get; set; } = null!;

        public byte[] Data { get; set; } = Array.Empty<byte>();
        public string ContentType { get; set; } = string.Empty;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }

    public static class JoinRequestStatus
    {
        public const string Pending = "pending";
        public const string Approved = "approved";
        public const string Rejected = "rejected";
        public const string Canceled = "canceled";
    }

    /// <summary>Solicitação de acesso: aprovada ou recusada pelo dono ou por um admin do grupo.</summary>
    public class GroupJoinRequest
    {
        public int GroupJoinRequestId { get; set; }
        public int GroupId { get; set; }
        public Group Group { get; set; } = null!;

        /// <summary>Quem pediu para entrar (sempre vem do token).</summary>
        public Guid MemberId { get; set; }

        /// <summary>Ver <see cref="JoinRequestStatus"/>.</summary>
        public string Status { get; set; } = JoinRequestStatus.Pending;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? DecidedAt { get; set; }
        public Guid? DecidedByMemberId { get; set; }
    }
}

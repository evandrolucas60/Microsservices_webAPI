using Microsoft.AspNetCore.Http;

namespace Intranet.Services.ComunidadesAPI.Models.DTOs
{
    public class ResponseDto
    {
        public object? Result { get; set; }
        public bool IsSuccess { get; set; } = true;
        public string Message { get; set; } = "";
    }

    public class GroupUpsertDto
    {
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
    }

    /// <summary>addMember(): inclui um colaborador no grupo.</summary>
    public class AddGroupMemberDto
    {
        public Guid MemberId { get; set; }
    }

    /// <summary>Promove ("admin") ou rebaixa ("member") um membro. Apenas o dono.</summary>
    public class ChangeGroupRoleDto
    {
        public string Role { get; set; } = string.Empty;
    }

    /// <summary>Informações públicas do grupo + situação do usuário atual.</summary>
    public class GroupDto
    {
        public int GroupId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public int TotalMembers { get; set; }
        public Guid CreatedByMemberId { get; set; }
        public DateTime CreatedAt { get; set; }

        /// <summary>Papel do usuário atual: "owner", "admin", "member" ou null (não participa).</summary>
        public string? MyRole { get; set; }
        public bool IsMember { get; set; }
        public bool IsOwner { get; set; }

        /// <summary>Pode aprovar solicitações e gerenciar membros/posts.</summary>
        public bool CanManage { get; set; }

        /// <summary>Pode promover/rebaixar admins (dono ou Admin global).</summary>
        public bool CanManageAdmins { get; set; }

        /// <summary>Pode editar nome/descrição/capa e excluir o grupo (dono ou superAdmin).</summary>
        public bool CanEdit { get; set; }

        /// <summary>Pode ver o conteúdo (posts, membros) — membros e Admin global.</summary>
        public bool CanView { get; set; }

        /// <summary>O usuário atual tem uma solicitação de acesso pendente.</summary>
        public bool HasPendingRequest { get; set; }

        /// <summary>Solicitações pendentes (preenchido apenas para quem pode aprovar).</summary>
        public int? PendingRequestsCount { get; set; }

        public DateTime? CoverUpdatedAt { get; set; }
        public bool HasCover => CoverUpdatedAt.HasValue;

        /// <summary>Versão da capa para invalidar o cache: GET api/groups/{id}/cover?v={CoverVersion}.</summary>
        public long? CoverVersion => CoverUpdatedAt?.Ticks;
    }

    public class GroupMemberDto
    {
        public Guid MemberId { get; set; }
        public string Role { get; set; } = string.Empty;
        public DateTime JoinedAt { get; set; }
    }

    public class GroupJoinRequestDto
    {
        public int GroupJoinRequestId { get; set; }
        public Guid MemberId { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    /// <summary>Nova publicação (multipart/form-data): texto e imagem opcional.</summary>
    public class GroupPostCreateDto
    {
        public string? Text { get; set; }
        public IFormFile? Image { get; set; }
    }

    public class GroupPostDto
    {
        public int GroupPostId { get; set; }
        public int GroupId { get; set; }
        public Guid MemberId { get; set; }
        public string Text { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public bool HasImage { get; set; }
        public int TotalLikes { get; set; }
        public int TotalComments { get; set; }
        public bool LikedByMe { get; set; }
        public bool CanDelete { get; set; }
    }

    public class GroupCommentUpsertDto
    {
        public string Text { get; set; } = string.Empty;
    }

    public class GroupCommentDto
    {
        public int GroupPostCommentId { get; set; }
        public int GroupPostId { get; set; }
        public Guid MemberId { get; set; }
        public string Text { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public bool CanDelete { get; set; }
    }

    /// <summary>Estado da curtida após o clique (fonte da verdade é o servidor).</summary>
    public class LikeStateDto
    {
        public int TotalLikes { get; set; }
        public bool LikedByMe { get; set; }
    }
}

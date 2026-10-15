namespace Intranet.Services.IdentidadeAPI.Models.DTOs;

public class MemberDto : Person
{
    public Guid MemberId { get; set; }
    public string? UserId { get; set; }
    public DateTime dateOfMembership { get; set; }
    public DateOnly? BirthDate { get; set; }

    // Somente leitura: o gestor é alterado em api/Hierarquia (ignorado no mapeamento DTO -> entidade)
    public Guid? GestorMemberId { get; set; }
}

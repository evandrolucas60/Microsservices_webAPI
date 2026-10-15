using System.ComponentModel.DataAnnotations;

namespace Intranet.Services.IdentidadeAPI.Models;

public class Member : Person
{
    [Key]
    public Guid MemberId { get; set; } = Guid.NewGuid();
    public string? UserId { get; set; }
    [Required]
    [DisplayFormat(DataFormatString = "{0:dd-MM-yyyy}", ApplyFormatInEditMode = true)]
    public DateTime dateOfMembership { get; set; }

    // Data de nascimento informada pelo próprio colaborador (usada na lista de aniversariantes)
    public DateOnly? BirthDate { get; set; }

    // Último login no portal (evento UsuarioAutenticado do AuthAPI); usado no painel "Most Recent Signin"
    public DateTime? LastLoginAt { get; set; }

    // Gestor direto (outro Member). Monta a hierarquia/organograma e será usado nas aprovações.
    // Gravado apenas por api/Hierarquia (sem ciclos, nunca o próprio membro); null = topo da hierarquia.
    public Guid? GestorMemberId { get; set; }
}

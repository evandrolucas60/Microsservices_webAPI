namespace Intranet.Services.IdentidadeAPI.Models.DTOs;

/// <summary>
/// Visão pública (para colaboradores autenticados) de um membro: usada nas listas de
/// aniversariantes, tempo de empresa e ramais. Não expõe ano de nascimento nem endereço.
/// </summary>
public class ColaboradorDto
{
    public Guid MemberId { get; set; }
    public string? Nome { get; set; }
    public string? Email { get; set; }
    public string? Telefone { get; set; }
    public string? Ramal { get; set; }
    public string? Cargo { get; set; }
    public string? Departamento { get; set; }
    public string? Loja { get; set; }
    public string? Foto { get; set; }

    // Aniversariantes: apenas dia/mês
    public int? DiaAniversario { get; set; }
    public int? MesAniversario { get; set; }

    // Tempo de empresa
    public DateTime? DataAdmissao { get; set; }
    public int? AnosDeEmpresa { get; set; }
}

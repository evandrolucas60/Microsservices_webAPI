using Escala5x2.BuildingBlocks.Contracts.Compliance;

namespace Compliance.Api.Domain;

/// <summary>
/// Regra de conformidade usada pelo motor e pelo avaliador (substitui as antigas LegalRuleVersion).
/// A mesma chave pode ter linhas com vigências diferentes (ex.: fases da EC 141); sem vigência = vale sempre.
/// O histórico de alterações fica na auditoria (evento ComplianceRuleChanged).
/// </summary>
public sealed class ComplianceRule
{
    public int Id { get; set; }
    /// <summary>jornada, intervalo, descanso, domingo, cobertura, compliance, feriado...</summary>
    public string Categoria { get; set; } = string.Empty;
    /// <summary>snake_case sem acento; única por vigência.</summary>
    public string Chave { get; set; } = string.Empty;
    public decimal Valor { get; set; }
    /// <summary>minutos | horas | dias | semanas | quantidade | booleano</summary>
    public string Unidade { get; set; } = string.Empty;
    public string Descricao { get; set; } = string.Empty;
    public string? BaseLegal { get; set; }
    /// <summary>Menor valor que a lei admite (afrouxar abaixo dele exige confirmação explícita do Admin).</summary>
    public decimal? PisoLegal { get; set; }
    /// <summary>Maior valor que a lei admite (afrouxar acima dele exige confirmação explícita do Admin).</summary>
    public decimal? TetoLegal { get; set; }
    public DateOnly? VigenciaInicio { get; set; }
    public DateOnly? VigenciaFim { get; set; }
    public RuleStatus Status { get; set; } = RuleStatus.Ativo;
    public RuleApplication Aplicacao { get; set; } = RuleApplication.Rigida;
    public string AtualizadoPor { get; set; } = string.Empty;
    public DateTime AtualizadoEm { get; set; } = DateTime.UtcNow;

    public bool Sobrepoe(DateOnly? inicio, DateOnly? fim) =>
        (VigenciaInicio ?? DateOnly.MinValue) <= (fim ?? DateOnly.MaxValue) &&
        (inicio ?? DateOnly.MinValue) <= (VigenciaFim ?? DateOnly.MaxValue);

    public bool ForaDoLimiteLegal(decimal valor) => valor < PisoLegal || valor > TetoLegal;

    public ComplianceRuleSnapshot ToSnapshot() =>
        new(Id, Categoria, Chave, Valor, Unidade, Descricao, VigenciaInicio, VigenciaFim, Status, Aplicacao, BaseLegal);
}

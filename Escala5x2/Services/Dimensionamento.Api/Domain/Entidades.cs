namespace Dimensionamento.Api.Domain;

/// <summary>Origem do perfil de demanda.</summary>
public static class OrigemPerfil
{
    /// <summary>Importado de CSV checkout;hora;dctos (totais do período ÷ dias).</summary>
    public const string Csv = "Csv";
    /// <summary>Curva digitada por hora (e opcionalmente por dia da semana).</summary>
    public const string Manual = "Manual";
    /// <summary>Exemplo de referência embutido no serviço (28-ASABRANCA, Consinco, 01–27/10/2026).</summary>
    public const string Exemplo = "Exemplo";
}

/// <summary>
/// Perfil de demanda de uma loja (e setor, opcional): documentos (chegadas) por hora, média por dia.
/// <see cref="CurvaJson"/> = lista de PontoCurva {hora, media, diaSemana?}; <see cref="FatoresDiaJson"/> = 7 fatores
/// (seg..dom) aplicados aos pontos sem dia da semana; <see cref="CheckoutsJson"/> = totais por check-out (importação CSV).
/// </summary>
public sealed class PerfilDemanda
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid StoreId { get; set; }
    public Guid? SectorId { get; set; }
    public string Nome { get; set; } = string.Empty;
    public string Origem { get; set; } = OrigemPerfil.Manual;
    public string? Observacao { get; set; }
    public DateOnly? PeriodoInicio { get; set; }
    public DateOnly? PeriodoFim { get; set; }
    /// <summary>Dias do período importado (divisor da média por dia).</summary>
    public int? Dias { get; set; }
    /// <summary>Total de documentos do período (CSV) ou soma da curva × 7 (manual).</summary>
    public double TotalDocumentos { get; set; }
    public string CurvaJson { get; set; } = "[]";
    public string? FatoresDiaJson { get; set; }
    public string? CheckoutsJson { get; set; }
    public string CriadoPor { get; set; } = string.Empty;
    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;
    public string? AtualizadoPor { get; set; }
    public DateTime? AtualizadoEm { get; set; }
}

/// <summary>Cenário de dimensionamento salvo: parâmetros da simulação e o último resultado (para comparar cenários).</summary>
public sealed class Cenario
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid StoreId { get; set; }
    public Guid? SectorId { get; set; }
    public Guid? PerfilDemandaId { get; set; }
    public string Nome { get; set; } = string.Empty;
    public string? Descricao { get; set; }
    /// <summary>SimulacaoRequest serializado.</summary>
    public string ParametrosJson { get; set; } = "{}";
    /// <summary>ResumoCenario do último cálculo.</summary>
    public string? ResumoJson { get; set; }
    /// <summary>SimulacaoResponse completo do último cálculo.</summary>
    public string? ResultadoJson { get; set; }
    public DateTime? CalculadoEm { get; set; }
    public string CriadoPor { get; set; } = string.Empty;
    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;
    public string? AtualizadoPor { get; set; }
    public DateTime? AtualizadoEm { get; set; }
}

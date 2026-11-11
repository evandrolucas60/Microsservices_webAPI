namespace Escala5x2.BuildingBlocks.Contracts.Http;

/// <summary>
/// Pedido do Scheduling.Api ao Employee.Api (POST /internal/planning/ferias-lg) antes de montar a fotografia: atualiza na
/// LG as férias dos colaboradores ativos da loja (do setor, ou só <paramref name="EmployeeIds"/>). O Employee.Api espera
/// até <paramref name="TimeoutSegundos"/>; se a LG demorar mais, responde "EmAndamento" e a atualização continua em
/// segundo plano (a geração segue com os períodos já gravados).
/// </summary>
public sealed record FeriasLgAtualizacaoRequest(Guid StoreId, Guid? SectorId, IReadOnlyList<Guid>? EmployeeIds, int? TimeoutSegundos);

/// <summary>
/// Resultado da atualização das férias para a geração. <paramref name="Status"/>: <see cref="FeriasLgStatus"/>.
/// <paramref name="UltimaSincronizacao"/>: a consulta mais antiga entre os colaboradores (UTC) e
/// <paramref name="UltimaSincronizacaoTexto"/> a mesma em "dd/MM HH:mm" no fuso da LG (null = nunca consultadas).
/// </summary>
public sealed record FeriasLgAtualizacaoResponse(
    string Status,
    int Colaboradores,
    int Consultados,
    int Falhas,
    int Alterados,
    DateTime? UltimaSincronizacao,
    string? UltimaSincronizacaoTexto,
    string? Mensagem);

/// <summary>Situações de <see cref="FeriasLgAtualizacaoResponse.Status"/> e o aviso da geração.</summary>
public static class FeriasLgStatus
{
    /// <summary>Integração de férias não configurada (LG:Operacoes:Ferias): férias só pelo cadastro manual, sem aviso.</summary>
    public const string NaoConfigurada = "NaoConfigurada";
    /// <summary>Consultadas agora, sem falhas.</summary>
    public const string Atualizada = "Atualizada";
    /// <summary>Consultadas há pouco (LG:Ferias:ValidadeMinutos): nenhuma chamada à LG.</summary>
    public const string Recente = "Recente";
    /// <summary>Consultadas agora, mas algumas matrículas falharam (essas ficam com o que já estava gravado).</summary>
    public const string Parcial = "Parcial";
    /// <summary>A LG não respondeu no tempo da geração: segue em segundo plano.</summary>
    public const string EmAndamento = "EmAndamento";
    /// <summary>A atualização falhou (LG fora do ar, configuração recusada...).</summary>
    public const string Falha = "Falha";

    /// <summary>
    /// Aviso da geração (null = nada a avisar). <paramref name="r"/> null = o Employee.Api não respondeu: a geração segue
    /// com as férias já registradas. Nunca bloqueia a geração.
    /// </summary>
    public static string? Aviso(FeriasLgAtualizacaoResponse? r)
    {
        const string Prefixo = "Férias da LG não puderam ser atualizadas agora";
        if (r is null) return $"{Prefixo}; usando os períodos de férias já registrados.";
        var uso = r.UltimaSincronizacaoTexto is { Length: > 0 } t
            ? $"usando a última sincronização de {t}"
            : "usando os períodos de férias já registrados";
        return r.Status switch
        {
            NaoConfigurada or Atualizada or Recente => null,
            Parcial when r.Falhas > 0 => $"Férias da LG de {r.Falhas} colaborador{(r.Falhas == 1 ? "" : "es")} não puderam ser atualizadas agora; {uso}.",
            Parcial => null,
            _ => $"{Prefixo}; {uso}."
        };
    }
}

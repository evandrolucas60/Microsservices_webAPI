using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Escala5x2.BuildingBlocks.Contracts.Compliance;

namespace Store.Api.Data;

/// <summary>
/// Lê regras do cadastro de conformidade (Compliance.Api /internal/rules): o intervalo padrão dos turnos do catálogo e as
/// regras do período para a prévia dos turnos automáticos.
/// </summary>
public sealed class ComplianceRulesClient(HttpClient http, ILogger<ComplianceRulesClient> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    /// <summary>Linhas (ativas e bloqueadas) com vigência no período, como na fotografia da escala; null = Compliance indisponível.</summary>
    public async Task<List<ComplianceRuleSnapshot>?> RulesAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        try
        {
            return await http.GetFromJsonAsync<List<ComplianceRuleSnapshot>>($"/internal/rules?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}", Json, ct) ?? [];
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning("Não foi possível ler as regras de {De} a {Ate} no Compliance.Api ({Motivo})", from, to, ex.Message);
            return null;
        }
    }

    /// <summary>Valor ativo da regra na data, ou null (regra inexistente/bloqueada ou Compliance indisponível).</summary>
    public async Task<decimal?> ActiveValueAsync(string chave, DateOnly date, CancellationToken ct = default)
    {
        try
        {
            var rules = await http.GetFromJsonAsync<List<ComplianceRuleSnapshot>>(
                $"/internal/rules?from={date:yyyy-MM-dd}&to={date:yyyy-MM-dd}", Json, ct) ?? [];
            return rules.Where(r => r.Chave == chave && r.Status == RuleStatus.Ativo && r.Contem(date))
                .OrderByDescending(r => r.VigenciaInicio ?? DateOnly.MinValue).FirstOrDefault()?.Valor;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning("Não foi possível ler a regra {Chave} no Compliance.Api ({Motivo})", chave, ex.Message);
            return null;
        }
    }
}

using System.Globalization;
using System.Text;
using System.Text.Json;
using Escala5x2.BuildingBlocks.Contracts.Scheduling;
using Escala5x2.BuildingBlocks.Shared.Auth;
using Escala5x2.BuildingBlocks.Shared.Web;
using Microsoft.EntityFrameworkCore;
using Scheduling.Api.Clients;
using Scheduling.Api.Data;

namespace Scheduling.Api.Endpoints;

/// <summary>
/// Logs para download (só Admin = superAdmin da intranet): o log das opções de escala avaliadas pelo motor numa escala
/// (SolucoesMotorJson, CSV ou JSON) e os arquivos de log do Serilog deste serviço e do Optimization.Worker
/// (Logs:PastasExtras no appsettings; ver <see cref="LogFiles"/>).
/// </summary>
public static class LogEndpoints
{
    /// <summary>Indicadores por pessoa na ordem da tela; chaves novas do Worker vão depois, em ordem alfabética.</summary>
    private static readonly (string Chave, string Rotulo)[] Indicadores =
    [
        ("aberturas", "Aberturas/pessoa (mín–máx)"),
        ("fechamentos", "Fechamentos/pessoa (mín–máx)"),
        ("domingos", "Domingos/pessoa (mín–máx)"),
    ];

    private static readonly JsonSerializerOptions JsonIndentado = new(Json.Options) { WriteIndented = true };

    public static void MapLogEndpoints(this IEndpointRouteBuilder app)
    {
        // GET /api/schedules/{id}/solucoes/log?formato=csv|json — uma linha por solução encontrada pelo CP-SAT
        app.MapGet("/api/schedules/{id:guid}/solucoes/log", async (Guid id, string? formato, SchedulingDbContext db, CancellationToken ct) =>
        {
            var formatoArquivo = string.IsNullOrWhiteSpace(formato) ? "csv" : formato.Trim().ToLowerInvariant();
            if (formatoArquivo is not ("csv" or "json")) return ServiceDefaults.ValidationError("formato", "Use formato=csv ou formato=json.");

            // Só as colunas necessárias: a fotografia e a conformidade (jsonb grandes) ficam no banco
            var s = await db.Schedules.AsNoTracking().Where(x => x.Id == id)
                .Select(x => new { x.Id, x.StoreName, x.SectorName, x.StartDate, x.EndDate, x.Weeks, x.Version, x.SolverStatus, x.SolucoesMotorJson })
                .FirstOrDefaultAsync(ct);
            if (s is null) return Results.NotFound();

            EngineSolutionsSummary? resumo = null;
            if (!string.IsNullOrEmpty(s.SolucoesMotorJson))
            {
                try
                {
                    resumo = JsonSerializer.Deserialize<EngineSolutionsSummary>(s.SolucoesMotorJson, Json.Options);
                }
                catch (JsonException)
                {
                    // registro ilegível: tratado como sem registro
                }
            }
            if (resumo is null)
                return Results.Problem(statusCode: StatusCodes.Status404NotFound,
                    detail: "Esta escala não tem o log do motor (escala manual, gerada por um Worker antigo ou sem geração entregue).");

            var nome = $"opcoes-motor-{s.StartDate:yyyyMMdd}-v{s.Version}-{s.Id.ToString("N")[..8]}";
            if (formatoArquivo == "json")
            {
                var conteudo = new
                {
                    scheduleId = s.Id, loja = s.StoreName, setor = s.SectorName, inicio = s.StartDate, fim = s.EndDate, semanas = s.Weeks,
                    versao = s.Version, resultadoMotor = s.SolverStatus, exportadoEm = DateTime.UtcNow, resumo,
                };
                return Results.File(JsonSerializer.SerializeToUtf8Bytes(conteudo, JsonIndentado), "application/json; charset=utf-8", $"{nome}.json");
            }
            return Results.File(Csv.Codificacao.GetPreamble().Concat(Csv.Codificacao.GetBytes(SolucoesCsv(resumo))).ToArray(),
                Csv.ContentType, $"{nome}.csv");
        }).RequireAuthorization(Policies.Admin);

        // Arquivos do Serilog: /api/schedules/logs (gateway: /api/escala5x2/schedules/logs)
        app.MapLogFileEndpoints("/api/schedules");
    }

    /// <summary>
    /// CSV do log do motor: nº, tempo, objetivo, melhor limite, gap, cada componente do objetivo (rótulo), indicadores por
    /// pessoa (mín–máx) e se é a escolhida.
    /// </summary>
    internal static string SolucoesCsv(EngineSolutionsSummary resumo)
    {
        var componentes = resumo.Componentes.Count > 0
            ? resumo.Componentes.Select(c => (c.Chave, c.Rotulo)).ToList()
            // Resumo sem a lista de componentes: as chaves que aparecem nas soluções, na ordem de exibição
            : resumo.Solucoes.SelectMany(x => x.Componentes.Keys).Distinct()
                .OrderBy(k => Array.IndexOf(EngineComponents.All, k) is var i && i < 0 ? int.MaxValue : i).ThenBy(k => k, StringComparer.Ordinal)
                .Select(k => (k, EngineComponents.Rotulo(k))).ToList();
        var chavesIndicadores = resumo.Solucoes.SelectMany(x => x.Indicadores.Keys).Distinct().ToHashSet();
        var indicadores = Indicadores.Where(i => chavesIndicadores.Contains(i.Chave)).ToList();
        indicadores.AddRange(chavesIndicadores.Where(k => Indicadores.All(i => i.Chave != k)).Order(StringComparer.Ordinal)
            .Select(k => (k, $"{k} (mín–máx)")));

        var sb = new StringBuilder();
        sb.AppendLine(Csv.Linha(
            new[] { "Nº", "Tempo (s)", "Objetivo", "Melhor limite", "Gap (%)" }
                .Concat(componentes.Select(c => c.Item2))
                .Concat(indicadores.Select(i => i.Item2))
                .Append("Escolhida")));
        foreach (var x in resumo.Solucoes.OrderBy(x => x.Numero))
        {
            sb.AppendLine(Csv.Linha(
                new[]
                {
                    x.Numero.ToString(CultureInfo.InvariantCulture), Csv.Numero(x.Segundos, "0.00"), Csv.Numero(x.Objetivo),
                    Csv.Numero(x.MelhorLimite, "0.##"), Csv.Numero(x.Gap * 100, "0.##"),
                }
                .Concat(componentes.Select(c => Csv.Numero(x.Componentes.TryGetValue(c.Item1, out var v) ? v : 0)))
                // "mín–máx" com travessão: com hífen o Excel leria "2-3" como data
                .Concat(indicadores.Select(i => x.Indicadores.TryGetValue(i.Item1, out var r) ? (r.Min == r.Max ? $"{r.Min}" : $"{r.Min}–{r.Max}") : ""))
                .Append(x.Escolhida ? "sim" : "não")));
        }
        return sb.ToString();
    }
}

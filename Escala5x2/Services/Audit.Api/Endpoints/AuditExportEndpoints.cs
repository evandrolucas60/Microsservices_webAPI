using System.Globalization;
using Audit.Api.Domain;
using Escala5x2.BuildingBlocks.Shared.Auth;
using Escala5x2.BuildingBlocks.Shared.Web;
using Microsoft.EntityFrameworkCore;

namespace Audit.Api.Endpoints;

/// <summary>
/// Exportação da auditoria em CSV (mesmos filtros de GET /api/audit, sem paginação) e arquivos de log do Serilog deste
/// serviço. Só Admin (superAdmin da intranet).
/// </summary>
public static class AuditExportEndpoints
{
    /// <summary>Teto de linhas por exportação (a tela filtra por evento/escala/período).</summary>
    public const int MaxLinhas = 200_000;

    /// <summary>Horário mostrado no CSV: o de Pernambuco (UTC−3, sem horário de verão); sem o fuso no servidor, UTC.</summary>
    private static readonly TimeZoneInfo? Fuso = ObterFuso();

    private static TimeZoneInfo? ObterFuso()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("America/Recife");
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return null;
        }
    }

    private static string Quando(DateTime utc) =>
        (Fuso is null ? utc : TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Fuso))
            .ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture);

    public static void MapAuditExportEndpoints(this IEndpointRouteBuilder app)
    {
        // GET /api/audit/exportar?type=&scheduleId=&from=&to= (gateway: /api/escala5x2/audit/exportar)
        app.MapGet("/api/audit/exportar", async (string? type, Guid? scheduleId, DateTime? from, DateTime? to, AuditDbContext db, HttpContext http) =>
        {
            var q = db.Entries.AsNoTracking().AsQueryable();
            if (!string.IsNullOrWhiteSpace(type)) q = q.Where(e => e.MessageType == type);
            if (scheduleId is { } sid) q = q.Where(e => e.ScheduleId == sid);
            if (from is { } f) q = q.Where(e => e.OccurredAt >= DateTime.SpecifyKind(f, DateTimeKind.Utc));
            if (to is { } t) q = q.Where(e => e.OccurredAt <= DateTime.SpecifyKind(t, DateTimeKind.Utc));

            var ct = http.RequestAborted;
            var nome = $"auditoria-escala5x2{(string.IsNullOrWhiteSpace(type) ? "" : $"-{type}")}-{DateTime.UtcNow:yyyyMMdd-HHmm}.csv";
            var disposition = new Microsoft.Net.Http.Headers.ContentDispositionHeaderValue("attachment");
            disposition.SetHttpFileName(nome);
            http.Response.ContentType = Csv.ContentType;
            http.Response.Headers.ContentDisposition = disposition.ToString();

            // Streaming: as linhas vão para a resposta conforme chegam do banco (sem montar o arquivo em memória)
            await using var writer = new StreamWriter(http.Response.Body, Csv.Codificacao, bufferSize: 16 * 1024, leaveOpen: true);
            await writer.WriteLineAsync(Csv.Linha(["Quando (horário de Recife)", "Evento", "Quem", "Escala", "Correlação", "Registrado em", "Detalhe"]));
            await foreach (var e in q.OrderByDescending(e => e.OccurredAt).Take(MaxLinhas).AsAsyncEnumerable().WithCancellation(ct))
            {
                await writer.WriteLineAsync(Csv.Linha([
                    Quando(e.OccurredAt), e.MessageType, e.Actor, e.ScheduleId?.ToString(), e.CorrelationId?.ToString(), Quando(e.RecordedAt), e.Payload,
                ]));
            }
            await writer.FlushAsync(ct);
            return Results.Empty;
        }).RequireAuthorization(Policies.Admin);

        // Arquivos do Serilog: /api/audit/logs (gateway: /api/escala5x2/audit/logs)
        app.MapLogFileEndpoints("/api/audit");
    }
}

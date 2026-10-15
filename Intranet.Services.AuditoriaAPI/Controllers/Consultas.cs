using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Intranet.Services.AuditoriaAPI.Controllers;

/// <summary>Auxiliares das consultas do painel: paginação (X-Pagination), períodos e dias.</summary>
internal static class Consultas
{
    public const int MaxPageSize = 100;
    public const int MaxDias = 365;

    public static (int PageNumber, int PageSize) Normalizar(int pageNumber, int pageSize) =>
        (Math.Max(pageNumber, 1), Math.Clamp(pageSize, 1, MaxPageSize));

    public static void EscreverPaginacao(HttpResponse response, int total, int pageNumber, int pageSize)
    {
        var totalPages = (int)Math.Ceiling(total / (double)pageSize);
        response.Headers["X-Pagination"] = JsonSerializer.Serialize(new
        {
            TotalCount = total,
            PageSize = pageSize,
            CurrentPage = pageNumber,
            TotalPages = totalPages,
            HasNext = pageNumber < totalPages,
            HasPrevious = pageNumber > 1,
        });
    }

    /// <summary>Datas sem fuso são tratadas como UTC (o PostgreSQL exige UTC em timestamptz).</summary>
    public static DateTime ParaUtc(DateTime data) =>
        data.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(data, DateTimeKind.Utc) : data.ToUniversalTime();

    /// <summary>
    /// Limite superior exclusivo do filtro "ate": data sem horário (00:00) inclui o dia inteiro.
    /// </summary>
    public static DateTime LimiteAte(DateTime ate)
    {
        var utc = ParaUtc(ate);
        return utc.TimeOfDay == TimeSpan.Zero ? utc.AddDays(1) : utc.AddTicks(1);
    }

    /// <summary>Primeiro dia (UTC, 00:00) de uma janela de "dias" dias terminando hoje.</summary>
    public static DateTime InicioJanela(int dias) => DateTime.UtcNow.Date.AddDays(-(dias - 1));

    public static int NormalizarDias(int dias) => Math.Clamp(dias, 1, MaxDias);

    public static IEnumerable<DateTime> Dias(DateTime inicio, int dias) =>
        Enumerable.Range(0, dias).Select(i => DateTime.SpecifyKind(inicio.AddDays(i), DateTimeKind.Utc));

    public static string FormatarDia(DateTime dia) => dia.ToString("yyyy-MM-dd");
}

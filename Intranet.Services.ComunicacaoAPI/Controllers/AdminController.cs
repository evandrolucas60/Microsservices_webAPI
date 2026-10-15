using System.Globalization;
using BuildingBlocks.Shared.Security;
using Intranet.Services.ComunicacaoAPI.Data;
using Intranet.Services.ComunicacaoAPI.Models;
using Intranet.Services.ComunicacaoAPI.Models.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Intranet.Services.ComunicacaoAPI.Controllers;

/// <summary>
/// Painel de gestão (superAdmin): apenas métricas de uso das mensagens e do chat.
/// As conversas são privadas: nenhum conteúdo, participante ou anexo é exposto aqui.
/// </summary>
[Route("api/admin")]
[ApiController]
[Authorize(Policy = AdminPolicies.SuperAdmin)]
public class AdminController : ControllerBase
{
    private readonly AppDbContext _db;

    public AdminController(AppDbContext db) => _db = db;

    /// <summary>
    /// Mensagens (caixa de entrada, incluindo respostas) e mensagens do chat (sem "chamar atenção")
    /// por dia (UTC) nos últimos N dias (1 a 365). Totais do período, com as conversas de chat iniciadas.
    /// </summary>
    [HttpGet("metricas")]
    public async Task<ActionResult> Metricas([FromQuery] int dias = 30, CancellationToken ct = default)
    {
        dias = Math.Clamp(dias, 1, 365);
        var inicio = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(-(dias - 1)), DateTimeKind.Utc);

        // Só as datas (nunca o texto); o agrupamento por dia UTC é feito aqui
        var mensagens = await _db.messages.AsNoTracking()
            .Where(m => m.CreatedAt >= inicio)
            .Select(m => m.CreatedAt)
            .ToListAsync(ct);
        var mensagensChat = await _db.chatMessages.AsNoTracking()
            .Where(m => m.CreatedAt >= inicio && m.Kind != ChatMessageKinds.Nudge)
            .Select(m => m.CreatedAt)
            .ToListAsync(ct);
        var conversasChat = await _db.conversations.AsNoTracking()
            .CountAsync(c => c.CreatedAt >= inicio, ct);

        var mensagensPorDia = ContarPorDia(mensagens);
        var chatPorDia = ContarPorDia(mensagensChat);

        var porDia = Enumerable.Range(0, dias)
            .Select(i => inicio.AddDays(i))
            .Select(d => new
            {
                Dia = d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                Mensagens = mensagensPorDia.GetValueOrDefault(d),
                MensagensChat = chatPorDia.GetValueOrDefault(d),
            })
            .ToList();

        return Ok(new ResponseDto
        {
            Result = new
            {
                PorDia = porDia,
                Totais = new
                {
                    Mensagens = mensagens.Count,
                    MensagensChat = mensagensChat.Count,
                    ConversasChat = conversasChat,
                },
            },
        });
    }

    private static Dictionary<DateTime, int> ContarPorDia(IEnumerable<DateTime> datas) =>
        datas
            .Select(d => (d.Kind == DateTimeKind.Local ? d.ToUniversalTime() : d).Date)
            .GroupBy(d => d)
            .ToDictionary(g => g.Key, g => g.Count());
}

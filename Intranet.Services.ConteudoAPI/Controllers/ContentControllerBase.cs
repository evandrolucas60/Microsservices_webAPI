using Intranet.Services.ConteudoAPI.Models.DTOs;
using Intranet.Services.ConteudoAPI.Services;
using Microsoft.AspNetCore.Mvc;

namespace Intranet.Services.ConteudoAPI.Controllers;

/// <summary>
/// Base dos controllers de conteúdo: identidade do colaborador sempre a partir do token
/// e notificação do autor quando outra pessoa interage (curtida/comentário).
/// </summary>
public abstract class ContentControllerBase : ControllerBase
{
    protected const string PerfilNaoEncontrado = "Seu perfil de colaborador não foi encontrado. Abra a página de perfil e tente novamente.";

    protected readonly IMemberResolver Members;
    protected readonly INotificationClient Notifications;

    protected ContentControllerBase(IMemberResolver members, INotificationClient notifications)
    {
        Members = members;
        Notifications = notifications;
    }

    protected string? CurrentUserId => User.FindFirst("id")?.Value;

    protected string? BearerToken
    {
        get
        {
            var header = Request.Headers.Authorization.ToString();
            return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header["Bearer ".Length..].Trim() : null;
        }
    }

    /// <summary>MemberId do colaborador autenticado (IdentidadeAPI, com cache).</summary>
    protected async Task<Guid?> CurrentMemberIdAsync()
    {
        var userId = CurrentUserId;
        return userId is null ? null : await Members.GetMemberIdAsync(userId, BearerToken, HttpContext.RequestAborted);
    }

    protected static ResponseDto Fail(string message) => new() { IsSuccess = false, Message = message };

    /// <summary>Limite da lista de quem curtiu (as mais recentes primeiro).</summary>
    protected const int MaxLikers = 500;

    /// <summary>Monta a lista de quem curtiu com nome e foto (uma consulta ao IdentidadeAPI para todos).</summary>
    protected async Task<List<LikerDto>> ToLikersAsync(IReadOnlyList<(Guid MemberId, DateTime CreatedAt)> likes)
    {
        var membros = await Members.GetMembersAsync(likes.Select(l => l.MemberId), HttpContext.RequestAborted);
        return likes.Select(l => new LikerDto
        {
            MemberId = l.MemberId,
            Nome = membros.TryGetValue(l.MemberId, out var m) ? m.Nome : null,
            Foto = membros.TryGetValue(l.MemberId, out var f) ? f.Foto : null,
            CurtidoEm = l.CreatedAt,
        }).ToList();
    }

    /// <summary>
    /// Notifica o autor do conteúdo sobre a interação de outro colaborador
    /// (não notifica quando o autor interage com o próprio conteúdo).
    /// </summary>
    protected async Task NotifyAuthorAsync(Guid authorMemberId, Guid actorMemberId, Func<string, string> message, string category, string link)
    {
        if (authorMemberId == Guid.Empty || authorMemberId == actorMemberId) return;

        var actorName = await Members.GetNameAsync(actorMemberId, BearerToken, HttpContext.RequestAborted) ?? "Um colaborador";
        await Notifications.NotifyAsync(new[] { authorMemberId }, message(actorName), category, link, BearerToken, HttpContext.RequestAborted);
    }
}

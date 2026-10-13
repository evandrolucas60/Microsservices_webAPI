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

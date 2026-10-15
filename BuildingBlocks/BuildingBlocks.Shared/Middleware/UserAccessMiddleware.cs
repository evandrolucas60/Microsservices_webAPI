using BuildingBlocks.Shared.Security;
using Microsoft.AspNetCore.Http;
using Serilog.Context;

namespace BuildingBlocks.Shared.Middleware;

/// <summary>
/// Depois da autenticação: inclui o UserId nos logs da requisição e barra usuários bloqueados
/// pelo superAdmin (403) em qualquer endpoint autenticado do serviço.
/// </summary>
public class UserAccessMiddleware
{
    private readonly RequestDelegate _next;

    public UserAccessMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, IRestricoesUsuarioService restricoes)
    {
        var userId = context.User.GetUserId();
        if (userId is null)
        {
            await _next(context);
            return;
        }

        using (LogContext.PushProperty("UserId", userId))
        {
            var r = await restricoes.ObterAsync(userId, context.RequestAborted);
            if (r.Bloqueado)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new
                {
                    isSuccess = false,
                    message = "Sua conta está bloqueada. Procure a administração do portal.",
                    bloqueado = true,
                });
                return;
            }

            await _next(context);
        }
    }
}

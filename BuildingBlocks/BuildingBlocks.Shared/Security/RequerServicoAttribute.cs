using BuildingBlocks.Contracts.Administracao;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace BuildingBlocks.Shared.Security;

/// <summary>
/// Exige que o serviço não esteja restrito para o usuário (ex.: [RequerServico(ServicosRestringiveis.Chat)]).
/// Responde 403 com mensagem em português quando o superAdmin restringiu o serviço.
/// </summary>
public sealed class RequerServicoAttribute : TypeFilterAttribute
{
    public RequerServicoAttribute(string servico) : base(typeof(RequerServicoFilter))
    {
        Arguments = new object[] { servico };
    }
}

public sealed class RequerServicoFilter : IAsyncActionFilter
{
    private readonly string _servico;
    private readonly IRestricoesUsuarioService _restricoes;

    public RequerServicoFilter(string servico, IRestricoesUsuarioService restricoes)
    {
        _servico = servico;
        _restricoes = restricoes;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var userId = context.HttpContext.User.GetUserId();
        if (userId is not null)
        {
            var r = await _restricoes.ObterAsync(userId, context.HttpContext.RequestAborted);
            if (r.Bloqueado || r.Servicos.Contains(_servico, StringComparer.OrdinalIgnoreCase))
            {
                var nome = ServicosRestringiveis.Nomes.TryGetValue(_servico, out var n) ? n.ToLowerInvariant() : _servico;
                context.Result = new ObjectResult(new
                {
                    isSuccess = false,
                    message = r.Bloqueado
                        ? "Sua conta está bloqueada. Procure a administração do portal."
                        : $"Seu acesso a \"{nome}\" está restrito pela administração do portal.",
                })
                { StatusCode = StatusCodes.Status403Forbidden };
                return;
            }
        }

        await next();
    }
}

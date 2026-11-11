using BuildingBlocks.Contracts.Administracao;
using BuildingBlocks.Shared.Security;
using MassTransit;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Escala5x2.BuildingBlocks.Shared.Web;

/// <summary>
/// Publica <see cref="ErroRegistrado"/> (AuditoriaAPI → painel de gestão da intranet) para os erros não tratados,
/// como o ExceptionHandlingMiddleware dos serviços da intranet. Sempre devolve false: o UseExceptionHandler segue
/// com o comportamento padrão (log do erro e resposta 500 em ProblemDetails, que o front da Escala 5x2 lê).
/// A publicação não segura a resposta (o RabbitMQ fora do ar não atrasa o erro para o usuário).
/// </summary>
public sealed class ErroRegistradoExceptionHandler(IHostEnvironment environment, ILogger<ErroRegistradoExceptionHandler> logger, IBus? bus = null)
    : IExceptionHandler
{
    private static readonly TimeSpan TempoMaximoPublicacao = TimeSpan.FromSeconds(5);

    public ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        // Cliente desistiu da requisição ou requisição inválida (4xx): não é erro do serviço
        var abortada = exception is OperationCanceledException && context.RequestAborted.IsCancellationRequested;
        if (bus is null || abortada || exception is BadHttpRequestException) return ValueTask.FromResult(false);

        var detalhe = $"{exception.GetType().FullName}: {exception.Message}{Environment.NewLine}{exception.StackTrace}";
        var erro = new ErroRegistrado
        {
            Servico = environment.ApplicationName,
            UserId = context.User.GetUserId(),
            Metodo = context.Request.Method,
            Caminho = context.Request.Path.Value ?? string.Empty,
            Status = StatusCodes.Status500InternalServerError,
            Mensagem = "Ocorreu um erro interno. Tente novamente.",
            Excecao = detalhe.Length > 4000 ? detalhe[..4000] : detalhe,
            CorrelationId = context.TraceIdentifier,
            Em = DateTime.UtcNow,
        };
        _ = PublicarAsync(bus, erro);
        return ValueTask.FromResult(false);
    }

    private async Task PublicarAsync(IBus barramento, ErroRegistrado erro)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TempoMaximoPublicacao);
            await barramento.Publish(erro, timeout.Token);
        }
        catch (Exception ex)
        {
            logger.LogWarning("Não foi possível publicar o erro para a auditoria: {Motivo}", ex.Message);
        }
    }
}

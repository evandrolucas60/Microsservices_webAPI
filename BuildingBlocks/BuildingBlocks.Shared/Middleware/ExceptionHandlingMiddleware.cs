using BuildingBlocks.Shared.Exceptions;
using MassTransit;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace BuildingBlocks.Shared.Middleware;

/// <summary>
/// Converte exceções não tratadas em JSON { isSuccess: false, message } com o status adequado.
/// Deve ficar depois do UseCors: assim a resposta de erro mantém os cabeçalhos CORS e o navegador
/// mostra a mensagem em vez de um genérico "Failed to fetch". Detalhes técnicos só vão para o log.
/// </summary>
public class ExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;

    public ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // Cliente desistiu da requisição: nada a responder
        }
        catch (Exception ex) when (!context.Response.HasStarted)
        {
            var (status, message) = ex switch
            {
                AppException app => (app.StatusCode, app.Message),
                RequestTimeoutException => (StatusCodes.Status503ServiceUnavailable, "Um serviço interno não respondeu a tempo. Tente novamente."),
                BadHttpRequestException bad => (bad.StatusCode, bad.StatusCode == StatusCodes.Status413PayloadTooLarge
                    ? "O envio é maior que o permitido."
                    : "Requisição inválida."),
                _ => (StatusCodes.Status500InternalServerError, "Ocorreu um erro interno. Tente novamente."),
            };

            if (status >= 500) _logger.LogError(ex, "Erro ao processar {Method} {Path}", context.Request.Method, context.Request.Path);
            else _logger.LogWarning("Requisição recusada ({Status}) em {Method} {Path}: {Message}", status, context.Request.Method, context.Request.Path, ex.Message);

            context.Response.StatusCode = status;
            await context.Response.WriteAsJsonAsync(new { isSuccess = false, message, correlationId = context.TraceIdentifier });
        }
    }
}

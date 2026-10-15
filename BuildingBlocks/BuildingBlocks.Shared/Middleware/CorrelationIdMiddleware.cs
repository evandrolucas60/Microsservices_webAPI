using Microsoft.AspNetCore.Http;
using Serilog.Context;

namespace BuildingBlocks.Shared.Middleware;

/// <summary>
/// Id de correlação por requisição (cabeçalho X-Correlation-Id): reaproveita o recebido ou cria um,
/// devolve na resposta e inclui em todos os logs da requisição (propriedade CorrelationId).
/// </summary>
public class CorrelationIdMiddleware
{
    public const string HeaderName = "X-Correlation-Id";

    private readonly RequestDelegate _next;

    public CorrelationIdMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var received = context.Request.Headers[HeaderName].ToString();
        // Aceita apenas valores curtos e simples (evita injeção em logs)
        var correlationId = !string.IsNullOrWhiteSpace(received) && received.Length <= 64 && received.All(c => char.IsLetterOrDigit(c) || c is '-' or '_')
            ? received
            : context.TraceIdentifier;

        context.TraceIdentifier = correlationId;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        using (LogContext.PushProperty("CorrelationId", correlationId))
        {
            await _next(context);
        }
    }
}

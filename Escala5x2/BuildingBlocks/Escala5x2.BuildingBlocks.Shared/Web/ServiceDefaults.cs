using System.Text.Json.Serialization;
using BuildingBlocks.Shared.Logging;
using BuildingBlocks.Shared.Middleware;
using Escala5x2.BuildingBlocks.Shared.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Escala5x2.BuildingBlocks.Shared.Web;

public static class ServiceDefaults
{
    /// <summary>
    /// Padrão comum a todas as APIs: secrets, Serilog (mesmo padrão da intranet: console + logs/{serviço}-AAAAMMDD.log),
    /// ProblemDetails, OpenAPI, health e JWT.
    /// </summary>
    public static WebApplicationBuilder AddServiceDefaults(this WebApplicationBuilder builder)
    {
        // Docker secrets montados em /run/secrets/<Chave__Subchave> viram configuração.
        builder.Configuration.AddKeyPerFile("/run/secrets", optional: true, reloadOnChange: false);

        builder.AddSharedSerilog();
        builder.Services.AddProblemDetails();
        // Erros 5xx também vão para o painel de gestão da intranet (AuditoriaAPI); a resposta segue em ProblemDetails
        builder.Services.AddExceptionHandler<ErroRegistradoExceptionHandler>();
        builder.Services.AddOpenApi();
        builder.Services.AddHealthChecks();
        builder.Services.AddJwtAuthentication(builder.Configuration, builder.Environment);
        builder.Services.ConfigureHttpJsonOptions(o =>
            o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        return builder;
    }

    public static WebApplication UseServiceDefaults(this WebApplication app)
    {
        // X-Correlation-Id + "HTTP {RequestMethod} {RequestPath} respondeu {StatusCode} em {Elapsed} ms" (igual à intranet)
        app.UseSharedRequestLogging();
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.Use(async (context, next) =>
        {
            try
            {
                await next(context);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                // Cliente/gateway desistiu da requisição (ex.: durante a consulta da loja ao IdentidadeAPI ou de
                // bloqueio ao AuthAPI): não é erro do serviço e não há a quem responder
                if (!context.Response.HasStarted) context.Response.StatusCode = StatusCodes.Status499ClientClosedRequest;
            }
        });
        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
        }
        app.UseAuthentication();
        // Usuário bloqueado pelo superAdmin da intranet → 403 (consulta o AuthAPI pelo RabbitMQ, com cache)
        app.UseMiddleware<UserAccessMiddleware>();
        app.UseAuthorization();
        app.MapHealthChecks("/health").AllowAnonymous();
        return app;
    }

    /// <summary>Padrão para Workers (sem HTTP): secrets e Serilog (console + logs/{serviço}-AAAAMMDD.log).</summary>
    public static HostApplicationBuilder AddWorkerDefaults(this HostApplicationBuilder builder)
    {
        builder.Configuration.AddKeyPerFile("/run/secrets", optional: true, reloadOnChange: false);
        builder.AddSharedSerilog();
        return builder;
    }

    public static IResult ValidationError(string field, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });
}

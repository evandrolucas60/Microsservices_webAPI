using System.Net.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BuildingBlocks.Shared.Http;

public static class HttpClientBuilderExtensions
{
    /// <summary>
    /// Chamadas HTTP internas em desenvolvimento: aceita o certificado de desenvolvimento do .NET
    /// em localhost mesmo quando ele não está instalado como confiável (erro "UntrustedRoot").
    /// Em produção a validação é a padrão.
    /// </summary>
    public static IHttpClientBuilder AcceptLocalDevCertificate(this IHttpClientBuilder builder, IHostEnvironment environment)
    {
        if (!environment.IsDevelopment()) return builder;

        return builder.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (request, _, _, errors) =>
                errors == SslPolicyErrors.None || request.RequestUri?.IsLoopback == true,
        });
    }
}

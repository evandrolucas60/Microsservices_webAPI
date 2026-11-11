using System.Net.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Escala5x2.BuildingBlocks.Shared.Web;

public static class DevCertificate
{
    /// <summary>
    /// Chamadas internas em desenvolvimento (https://localhost:702x): aceita o certificado de
    /// desenvolvimento do .NET em localhost mesmo sem estar instalado como confiável.
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

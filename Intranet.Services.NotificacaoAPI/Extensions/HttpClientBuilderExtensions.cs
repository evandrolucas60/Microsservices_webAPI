using System.Net.Security;

namespace Intranet.Services.NotificacaoAPI.Extensions;

public static class HttpClientBuilderExtensions
{
    /// <summary>
    /// Chamadas entre serviços em desenvolvimento: aceita o certificado HTTPS de desenvolvimento
    /// do .NET em localhost mesmo quando ele não está instalado como confiável na máquina
    /// (sem isso, a chamada falha com "UntrustedRoot"). Em produção a validação é a padrão.
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
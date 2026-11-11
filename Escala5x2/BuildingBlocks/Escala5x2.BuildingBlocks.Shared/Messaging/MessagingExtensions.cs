using System.Reflection;
using BuildingBlocks.Contracts.Administracao;
using BuildingBlocks.Contracts.Identidade;
using BuildingBlocks.Shared.Security;
using Escala5x2.BuildingBlocks.Shared.Auth;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Escala5x2.BuildingBlocks.Shared.Messaging;

public static class MessagingExtensions
{
    /// <summary>Prefixo das filas: o RabbitMQ é o mesmo da intranet.</summary>
    public const string QueuePrefix = "escala5x2";

    /// <summary>
    /// MassTransit + RabbitMQ com nomes de fila em kebab-case (prefixo "escala5x2-"), retentativa com intervalos
    /// crescentes e envio automático para a fila "_error" (dead-letter) após esgotar as tentativas.
    /// As exchanges seguem o namespace dos contratos (Escala5x2.BuildingBlocks.Contracts.*), sem colidir
    /// com as mensagens da intranet.
    /// Nas APIs (AddServiceDefaults antes desta chamada) o mesmo bus também conversa com a intranet:
    /// loja do perfil (IdentidadeAPI) e bloqueio de usuários (AuthAPI), com os contratos de BuildingBlocks.Contracts.
    /// </summary>
    public static IServiceCollection AddEventBus(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<IBusRegistrationConfigurator>? configure = null)
    {
        var options = RabbitMqOptions.From(configuration);
        var usuariosIntranet = services.Any(d => d.ServiceType == typeof(IntranetBusMarker));

        services.AddMassTransit(x =>
        {
            x.SetEndpointNameFormatter(new KebabCaseEndpointNameFormatter(QueuePrefix, false));

            if (usuariosIntranet)
            {
                // Timeout curto (RabbitMq:RequestTimeoutSeconds, padrão 5 s): as consultas acontecem na autenticação
                var timeout = RequestTimeout.After(ms: (int)options.RequestTimeout.TotalMilliseconds);
                x.AddRequestClient<ObterMembroPorUsuario>(timeout);
                x.AddRequestClient<ObterRestricoesUsuario>(timeout);
                // Bloqueio alterado no painel de gestão: cada instância tem a sua fila temporária para
                // atualizar o cache local na hora (igual aos serviços da intranet)
                var app = Assembly.GetEntryAssembly()?.GetName().Name ?? "escala5x2";
                x.AddConsumer<RestricoesUsuarioAlteradasConsumer>()
                    .Endpoint(e =>
                    {
                        e.InstanceId = $"{app}-{Guid.NewGuid():N}"[..Math.Min(60, app.Length + 33)];
                        e.Temporary = true;
                    });
            }

            configure?.Invoke(x);

            x.UsingRabbitMq((context, cfg) =>
            {
                cfg.Host(options.Host, options.Port, options.VirtualHost, h =>
                {
                    h.Username(options.Username);
                    h.Password(options.Password);
                });

                cfg.UseMessageRetry(r => r.Intervals(
                    TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15)));

                cfg.ConfigureEndpoints(context);
            });
        });

        return services;
    }
}

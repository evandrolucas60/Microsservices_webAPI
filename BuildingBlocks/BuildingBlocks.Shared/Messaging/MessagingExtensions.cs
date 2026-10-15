using BuildingBlocks.Contracts.Identidade;
using MassTransit;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BuildingBlocks.Shared.Messaging;

public static class MessagingExtensions
{
    /// <summary>
    /// Mensageria assíncrona entre os serviços (MassTransit + RabbitMQ).
    /// configure: registra os consumers do serviço (ex.: x.AddConsumers(typeof(Program).Assembly)).
    /// Também registra os clientes de requisição ao IdentidadeAPI, o <see cref="IIdentidadeGateway"/>
    /// e o <see cref="INotificacaoPublisher"/>.
    /// </summary>
    public static WebApplicationBuilder AddMessaging(this WebApplicationBuilder builder, Action<IBusRegistrationConfigurator>? configure = null)
    {
        var options = builder.Configuration.GetSection(RabbitMqOptions.SectionName).Get<RabbitMqOptions>() ?? new RabbitMqOptions();
        builder.Services.AddSingleton(options);

        builder.Services.AddMassTransit(x =>
        {
            // Filas com nomes legíveis: obter-membro-por-usuario, notificacao-solicitada...
            x.SetKebabCaseEndpointNameFormatter();

            var timeout = RequestTimeout.After(s: Math.Max(options.RequestTimeoutSeconds, 1));
            x.AddRequestClient<ObterMembroPorUsuario>(timeout);
            x.AddRequestClient<ObterMembros>(timeout);
            x.AddRequestClient<ObterColaboradoresPorDepartamento>(timeout);
            x.AddRequestClient<ObterDestinatariosEmail>(timeout);

            configure?.Invoke(x);

            x.UsingRabbitMq((context, cfg) =>
            {
                cfg.Host(options.Host, options.Port, options.VirtualHost, h =>
                {
                    h.Username(options.Username);
                    h.Password(options.Password);
                });

                // Falhas transitórias no consumer: tenta de novo antes de mover para a fila de erro (_error)
                cfg.UseMessageRetry(r => r.Intervals(
                    TimeSpan.FromMilliseconds(500),
                    TimeSpan.FromSeconds(2),
                    TimeSpan.FromSeconds(5)));

                cfg.ConfigureEndpoints(context);
            });
        });

        // A API sobe mesmo com o RabbitMQ fora do ar (o MassTransit reconecta sozinho)
        builder.Services.AddOptions<MassTransitHostOptions>().Configure(o =>
        {
            o.WaitUntilStarted = false;
            o.StopTimeout = TimeSpan.FromSeconds(15);
        });

        builder.Services.AddMemoryCache();
        builder.Services.AddScoped<IIdentidadeGateway, IdentidadeGateway>();
        builder.Services.AddScoped<INotificacaoPublisher, NotificacaoPublisher>();

        return builder;
    }
}

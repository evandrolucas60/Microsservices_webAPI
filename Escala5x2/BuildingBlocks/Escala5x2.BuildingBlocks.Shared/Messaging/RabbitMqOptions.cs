using Microsoft.Extensions.Configuration;

namespace Escala5x2.BuildingBlocks.Shared.Messaging;

public sealed class RabbitMqOptions
{
    public const string Section = "RabbitMq";
    public string Host { get; set; } = "rabbitmq";
    public ushort Port { get; set; } = 9000;
    public string VirtualHost { get; set; } = "/";
    public string Username { get; set; } = "guest";
    public string Password { get; set; } = "guest";

    /// <summary>
    /// Tempo máximo de espera nas requisições aos serviços da intranet (IdentidadeAPI/AuthAPI), em segundos.
    /// Curto de propósito: a consulta acontece na autenticação e segura a requisição do usuário.
    /// </summary>
    public int RequestTimeoutSeconds { get; set; } = 5;

    /// <summary>Timeout efetivo das requisições à intranet (entre 1 e 60 s).</summary>
    public TimeSpan RequestTimeout => TimeSpan.FromSeconds(Math.Clamp(RequestTimeoutSeconds, 1, 60));

    /// <summary>
    /// Limite próprio de espera nas requisições à intranet (um pouco acima do <see cref="RequestTimeout"/>):
    /// com o RabbitMQ inacessível o MassTransit fica tentando enviar e o RequestTimeout não vale.
    /// </summary>
    public TimeSpan LimiteEspera => RequestTimeout + TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Chave de cache (IMemoryCache) que sinaliza, por 30 s, que uma requisição à intranet estourou o
    /// <see cref="LimiteEspera"/> (RabbitMQ sem conexão): as próximas consultas seguem sem esperar.
    /// </summary>
    public const string ChaveSemConexao = "escala5x2:rabbitmq-sem-conexao";

    public static RabbitMqOptions From(IConfiguration configuration) =>
        configuration.GetSection(Section).Get<RabbitMqOptions>() ?? new RabbitMqOptions();
}

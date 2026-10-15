namespace BuildingBlocks.Shared.Messaging;

/// <summary>Conexão com o RabbitMQ (seção "RabbitMq" do appsettings).</summary>
public class RabbitMqOptions
{
    public const string SectionName = "RabbitMq";

    public string Host { get; set; } = "localhost";
    public ushort Port { get; set; } = 5672;
    public string VirtualHost { get; set; } = "/";
    public string Username { get; set; } = "guest";
    public string Password { get; set; } = "guest";

    /// <summary>Tempo máximo de espera nas requisições entre serviços (segundos).</summary>
    public int RequestTimeoutSeconds { get; set; } = 10;
}

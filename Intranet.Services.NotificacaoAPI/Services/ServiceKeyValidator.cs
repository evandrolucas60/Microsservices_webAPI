using System.Security.Cryptography;
using System.Text;

namespace Intranet.Services.NotificacaoAPI.Services;

/// <summary>
/// Credencial de serviço (header X-Api-Key) exigida nos endpoints internos, além do JWT:
/// só os serviços da intranet podem criar notificações / disparar newsletters.
/// Configuração: Notificacao:ServiceKey (ou Newsletter:ServiceKey, mantida por compatibilidade).
/// </summary>
public class ServiceKeyValidator
{
    public const string HeaderName = "X-Api-Key";

    private readonly IConfiguration _configuration;

    public ServiceKeyValidator(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public bool IsValid(HttpRequest request)
    {
        var expected = _configuration["Notificacao:ServiceKey"];
        if (string.IsNullOrWhiteSpace(expected)) expected = _configuration["Newsletter:ServiceKey"];

        var received = request.Headers[HeaderName].ToString();
        if (string.IsNullOrWhiteSpace(expected) || string.IsNullOrEmpty(received)) return false;

        // Comparação em tempo constante
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(received));
    }
}

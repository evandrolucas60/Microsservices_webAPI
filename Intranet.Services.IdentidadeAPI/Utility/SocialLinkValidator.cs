namespace Intranet.Services.IdentidadeAPI.Utility;

/// <summary>
/// Valida e normaliza os links de redes sociais do perfil.
/// Vazio → null; senão precisa ser uma URL absoluta http/https (http é convertido para https)
/// cujo host pertence à rede correspondente (inclui subdomínios como www. e m.).
/// </summary>
public static class SocialLinkValidator
{
    public const int MaxLength = 300;

    public static readonly string[] TwitterHosts = { "x.com", "twitter.com" };
    public static readonly string[] FacebookHosts = { "facebook.com", "fb.com" };
    public static readonly string[] InstagramHosts = { "instagram.com" };
    public static readonly string[] LinkedinHosts = { "linkedin.com" };

    /// <summary>
    /// Devolve true quando o valor é válido; <paramref name="normalized"/> recebe a URL a gravar
    /// (ou null quando vazio) e <paramref name="error"/> a mensagem em caso de falha.
    /// </summary>
    public static bool TryNormalize(string? raw, string networkName, string[] allowedHosts, out string? normalized, out string? error)
    {
        normalized = null;
        error = null;

        var value = raw?.Trim();
        if (string.IsNullOrEmpty(value)) return true;

        if (value.Length > MaxLength)
        {
            error = $"O link do {networkName} deve ter no máximo {MaxLength} caracteres.";
            return false;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            error = $"Informe o link completo do {networkName}, começando com https://.";
            return false;
        }

        // Sem usuário/senha embutidos nem porta fora do padrão
        if (!string.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort)
        {
            error = $"Link do {networkName} inválido.";
            return false;
        }

        var host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
        var hostOk = allowedHosts.Any(h => host == h || host.EndsWith("." + h, StringComparison.Ordinal));
        if (!hostOk)
        {
            error = $"O link do {networkName} deve ser de {string.Join(" ou ", allowedHosts)}.";
            return false;
        }

        var builder = new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps, Port = -1 };
        normalized = builder.Uri.AbsoluteUri;

        if (normalized.Length > MaxLength)
        {
            error = $"O link do {networkName} deve ter no máximo {MaxLength} caracteres.";
            normalized = null;
            return false;
        }

        return true;
    }
}

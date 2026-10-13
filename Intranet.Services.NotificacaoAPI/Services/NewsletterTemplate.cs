using System.Net;
using Intranet.Services.NotificacaoAPI.Models;

namespace Intranet.Services.NotificacaoAPI.Services;

/// <summary>
/// Monta o e-mail da newsletter. Envia apenas título, resumo e capa (não o HTML do artigo):
/// todo texto é codificado, então o conteúdo do artigo não consegue injetar HTML no e-mail.
/// </summary>
public static class NewsletterTemplate
{
    private static string TypeLabel(string type) => type switch
    {
        "video" => "Vídeo",
        "informativo" => "Informativo",
        _ => "Artigo",
    };

    public static string Subject(ArticleNewsletterRequest a) =>
        // MailMessage.Subject não aceita CR/LF
        $"[Arco-Net] {TypeLabel(a.Type)}: {a.Title.ReplaceLineEndings(" ")}";

    public static string ArticleUrl(ArticleNewsletterRequest a, string portalUrl) =>
        $"{portalUrl.TrimEnd('/')}/artigos/{a.ArticleId}";

    public static string Html(ArticleNewsletterRequest a, string? authorName, string? recipientName, string portalUrl)
    {
        string E(string? s) => WebUtility.HtmlEncode(s ?? string.Empty);

        var url = E(ArticleUrl(a, portalUrl));
        var cover = Uri.TryCreate(a.CoverImageUrl, UriKind.Absolute, out var coverUri) && coverUri.Scheme == Uri.UriSchemeHttps
            ? $"<img src=\"{E(a.CoverImageUrl)}\" alt=\"\" width=\"560\" style=\"display:block;width:100%;max-width:560px;height:auto;border-radius:6px;margin:0 0 16px\">"
            : string.Empty;
        var greeting = string.IsNullOrWhiteSpace(recipientName) ? "Olá!" : $"Olá, {E(recipientName)}!";
        var author = string.IsNullOrWhiteSpace(authorName) ? string.Empty : $"<p style=\"margin:0 0 16px;color:#6b7280;font-size:13px\">Por {E(authorName)}</p>";
        var summary = string.IsNullOrWhiteSpace(a.Summary) ? string.Empty : $"<p style=\"margin:0 0 20px;font-size:15px;line-height:1.5\">{E(a.Summary)}</p>";

        return $"""
            <!doctype html>
            <html lang="pt-BR">
            <body style="margin:0;padding:0;background:#f3f4f6;font-family:Arial,Helvetica,sans-serif;color:#111827">
              <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="background:#f3f4f6;padding:24px 0">
                <tr><td align="center">
                  <table role="presentation" width="600" cellpadding="0" cellspacing="0" style="max-width:600px;width:100%;background:#ffffff;border-radius:8px">
                    <tr><td style="padding:24px 20px">
                      <p style="margin:0 0 4px;color:#6b7280;font-size:13px">{greeting} Há um novo {E(TypeLabel(a.Type).ToLowerInvariant())} na Arco-Net:</p>
                      <h1 style="margin:8px 0 8px;font-size:22px;line-height:1.3">{E(a.Title)}</h1>
                      {author}
                      {cover}
                      {summary}
                      <a href="{url}" style="display:inline-block;background:#0d6efd;color:#ffffff;text-decoration:none;padding:10px 18px;border-radius:6px;font-weight:bold">Ler na Arco-Net</a>
                    </td></tr>
                    <tr><td style="padding:12px 20px 20px;color:#9ca3af;font-size:12px">
                      Você recebe este e-mail por ser colaborador cadastrado na intranet Arco-Net.
                    </td></tr>
                  </table>
                </td></tr>
              </table>
            </body>
            </html>
            """;
    }

    public static string PlainText(ArticleNewsletterRequest a, string? authorName, string portalUrl)
    {
        var author = string.IsNullOrWhiteSpace(authorName) ? string.Empty : $"Por {authorName}\n";
        return $"Novo {TypeLabel(a.Type).ToLowerInvariant()} na Arco-Net: {a.Title}\n{author}\n{a.Summary}\n\nLeia em: {ArticleUrl(a, portalUrl)}\n";
    }
}

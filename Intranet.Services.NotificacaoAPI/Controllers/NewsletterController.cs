using Intranet.Services.NotificacaoAPI.Models;
using Intranet.Services.NotificacaoAPI.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Intranet.Services.NotificacaoAPI.Controllers;

/// <summary>Disparo de newsletters (chamado pelo ConteudoAPI ao publicar um artigo).</summary>
[Route("api/newsletter")]
[ApiController]
[Authorize]
public class NewsletterController : ControllerBase
{
    private readonly RecipientsClient _recipients;
    private readonly NewsletterQueue _queue;
    private readonly IConfiguration _configuration;
    private readonly ILogger<NewsletterController> _logger;

    public NewsletterController(RecipientsClient recipients, NewsletterQueue queue, IConfiguration configuration, ILogger<NewsletterController> logger)
    {
        _recipients = recipients;
        _queue = queue;
        _configuration = configuration;
        _logger = logger;
    }

    private bool HasValidServiceKey()
    {
        var expected = _configuration["Newsletter:ServiceKey"];
        var received = Request.Headers["X-Api-Key"].ToString();
        if (string.IsNullOrWhiteSpace(expected) || string.IsNullOrEmpty(received)) return false;

        // Comparação em tempo constante
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(expected),
            System.Text.Encoding.UTF8.GetBytes(received));
    }

    /// <summary>
    /// Busca os destinatários (com o token de quem publicou) e coloca o envio na fila.
    /// Responde 202 imediatamente; os e-mails saem em segundo plano.
    /// </summary>
    [HttpPost("articles")]
    public async Task<ActionResult> QueueArticle([FromBody] ArticleNewsletterRequest request, CancellationToken ct)
    {
        // Além do JWT, exige a chave de serviço: colaboradores não podem disparar e-mail em massa diretamente
        if (!HasValidServiceKey())
        {
            _logger.LogWarning("Pedido de newsletter recusado: chave de serviço ausente ou inválida.");
            return Forbid();
        }

        if (request.ArticleId <= 0 || string.IsNullOrWhiteSpace(request.Title))
            return BadRequest("Artigo inválido.");

        var header = Request.Headers.Authorization.ToString();
        var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header["Bearer ".Length..].Trim() : null;

        IReadOnlyList<Recipient> recipients;
        try
        {
            recipients = await _recipients.GetAllAsync(token, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Não foi possível obter os destinatários da newsletter do artigo {ArticleId}", request.ArticleId);
            return StatusCode(StatusCodes.Status502BadGateway, "Não foi possível obter os destinatários.");
        }

        var authorName = recipients.FirstOrDefault(r => r.MemberId == request.AuthorMemberId)?.Nome;

        await _queue.EnqueueAsync(new NewsletterJob(request, authorName, recipients), ct);
        _logger.LogInformation("Newsletter do artigo {ArticleId} enfileirada para {Count} colaboradores.", request.ArticleId, recipients.Count);

        return Accepted(new { request.ArticleId, Recipients = recipients.Count });
    }
}

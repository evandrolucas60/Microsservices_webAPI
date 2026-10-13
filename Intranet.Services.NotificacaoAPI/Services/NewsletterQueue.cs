using System.Threading.Channels;
using Intranet.Services.NotificacaoAPI.Models;

namespace Intranet.Services.NotificacaoAPI.Services;

/// <summary>
/// Fila em memória de newsletters a enviar. Observação: trabalhos pendentes são perdidos
/// se o serviço reiniciar durante o envio (para garantia de entrega, usar fila persistente).
/// </summary>
public class NewsletterQueue
{
    private readonly Channel<NewsletterJob> _channel =
        Channel.CreateBounded<NewsletterJob>(new BoundedChannelOptions(100)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });

    public ValueTask EnqueueAsync(NewsletterJob job, CancellationToken ct) => _channel.Writer.WriteAsync(job, ct);

    public IAsyncEnumerable<NewsletterJob> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}

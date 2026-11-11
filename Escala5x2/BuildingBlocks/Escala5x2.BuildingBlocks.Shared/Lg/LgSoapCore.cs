using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Escala5x2.BuildingBlocks.Shared.Lg;

/// <summary>
/// Transporte SOAP 1.1 da LG (HttpClient + XDocument, sem proxy WCF), comum a todas as operações: endpoint e SOAPAction
/// por operação (<see cref="LgOptions.UrlDe"/>/<see cref="LgOptions.SoapActionDe"/>), timeout por chamada, retry com
/// backoff exponencial em erro transitório, SOAP Fault/erros HTTP com mensagem clara e paginação 1..TotalDePaginas.
/// Credenciais vão só no cabeçalho SOAP e NUNCA são registradas em log; o envelope e a resposta (que podem trazer CPF)
/// também não vão para o log.
/// </summary>
public sealed class LgSoapCore(HttpClient http, IOptionsMonitor<LgOptions> options, ILogger logger)
{
    /// <summary>Espera entre tentativas (substituível nos testes).</summary>
    public Func<TimeSpan, CancellationToken, Task> Esperar { get; set; } = Task.Delay;

    public LgOptions Opcoes => options.CurrentValue;

    /// <summary>
    /// Uma página, com retry curto (backoff exponencial) para erro transitório. <paramref name="envelope"/> monta a
    /// requisição com as opções vigentes; <paramref name="ler"/> converte a resposta (lança <see cref="LgIntegracaoException"/>).
    /// </summary>
    public async Task<T> ConsultarPaginaAsync<T>(LgOperacao op, int pagina, Func<LgOptions, string> envelope, Func<string, T> ler,
        CancellationToken ct)
    {
        var o = options.CurrentValue;
        if (!o.ConfiguradaPara(op)) throw new LgIntegracaoException(o.MensagemNaoConfigurada(op));
        var tentativas = Math.Max(1, o.Tentativas);
        for (var tentativa = 1; ; tentativa++)
        {
            try
            {
                return ler(await EnviarAsync(o, op, pagina, envelope(o), ct));
            }
            catch (LgIntegracaoException ex) when (ex.Transitorio && tentativa < tentativas && !ct.IsCancellationRequested)
            {
                var espera = TimeSpan.FromMilliseconds(Math.Max(0, o.EsperaInicialMs) * Math.Pow(2, tentativa - 1));
                logger.LogWarning("LG: {Operacao}, página {Pagina}, tentativa {Tentativa}/{Total} falhou ({Motivo}); nova tentativa em {Espera} ms",
                    op.Chave, pagina, tentativa, tentativas, ex.Message, (int)espera.TotalMilliseconds);
                await Esperar(espera, ct);
            }
        }
    }

    private async Task<string> EnviarAsync(LgOptions o, LgOperacao op, int pagina, string envelope, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, o.TimeoutSegundos)));
        using var request = new HttpRequestMessage(HttpMethod.Post, o.UrlDe(op))
        {
            Content = new StringContent(envelope, Encoding.UTF8, "text/xml")
        };
        // SOAP 1.1: SOAPAction entre aspas
        var action = o.SoapActionDe(op);
        request.Headers.TryAddWithoutValidation("SOAPAction", action.StartsWith('"') ? action : $"\"{action}\"");

        HttpResponseMessage response;
        string corpo;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token);
            corpo = await response.Content.ReadAsStringAsync(timeout.Token);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new LgIntegracaoException($"A LG não respondeu em {Math.Max(5, o.TimeoutSegundos)} s (página {pagina}).", transitorio: true, ex);
        }
        catch (HttpRequestException ex)
        {
            throw new LgIntegracaoException($"Falha de comunicação com a LG (página {pagina}): {ex.Message}", transitorio: true, ex);
        }

        using (response)
        {
            // SOAP Fault costuma vir com HTTP 500: a mensagem da LG vale mais que o status (e não adianta repetir)
            if (!response.IsSuccessStatusCode && !corpo.Contains("Fault", StringComparison.Ordinal))
            {
                var status = (int)response.StatusCode;
                var transitorio = response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || status >= 500;
                var chaveUrl = op.Chave == LgOperacao.Colaboradores.Chave ? "LG:Url" : $"LG:Operacoes:{op.Chave}:Url (ou LG:Url)";
                throw new LgIntegracaoException(
                    status is 401 or 403 ? $"A LG recusou o acesso (HTTP {status}): confira LG:Usuario, LG:Senha e LG:GuidTenant."
                    : status == 404 ? $"Serviço da LG não encontrado (HTTP 404): confira {chaveUrl}."
                    : $"A LG respondeu HTTP {status} {response.ReasonPhrase} (página {pagina}).",
                    transitorio);
            }
            return corpo;
        }
    }

    /// <summary>
    /// Todas as páginas (PaginaAtual 1..TotalDePaginas). <paramref name="pagina"/>(n) lê a página n;
    /// <paramref name="progresso"/>(página, total). <paramref name="descricao"/> só para o log (ex.: "empresa 1").
    /// </summary>
    public async Task<LgConsultaDe<T>> ConsultarTodasAsync<T>(LgOperacao op, Func<int, CancellationToken, Task<LgPaginaDe<T>>> pagina,
        Action<int, int>? progresso, CancellationToken ct, string? descricao = null)
    {
        var o = options.CurrentValue;
        var primeira = await pagina(1, ct);
        var total = Math.Max(1, primeira.TotalDePaginas);
        if (total > o.MaxPaginas)
            throw new LgIntegracaoException($"A LG informou {total} páginas, acima do limite LG:MaxPaginas ({o.MaxPaginas}).");
        progresso?.Invoke(1, total);
        var itens = new List<T>(primeira.Itens);
        for (var p = 2; p <= total; p++)
        {
            ct.ThrowIfCancellationRequested();
            var proxima = await pagina(p, ct);
            itens.AddRange(proxima.Itens);
            progresso?.Invoke(p, total);
        }
        logger.LogInformation("LG: {Operacao}{Descricao}: {Paginas} página(s), {Registros} registro(s) (TotalGeral {TotalGeral})",
            op.Chave, descricao is null ? "" : $" ({descricao})", total, itens.Count, primeira.TotalGeral);
        return new LgConsultaDe<T>(itens, total, primeira.TotalGeral);
    }
}

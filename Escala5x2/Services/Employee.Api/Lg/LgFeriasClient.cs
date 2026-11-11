using System.Collections.Concurrent;
using System.Xml.Linq;
using Escala5x2.BuildingBlocks.Shared.Lg;
using Microsoft.Extensions.Options;

namespace Employee.Api.Lg;

/// <summary>Período de férias de um contrato na LG: <paramref name="Inicio"/> = DataDaOcorrencia, <paramref name="Fim"/> = DataFim (inclusive).</summary>
public sealed record LgFeriasPeriodo(DateOnly Inicio, DateOnly Fim);

/// <summary>Férias de um contrato. <paramref name="Invalidos"/>: FeriasParcial sem data ou com fim antes do início (ignorados).</summary>
public sealed record LgFeriasConsulta(IReadOnlyList<LgFeriasPeriodo> Periodos, int Invalidos);

/// <summary>Resultado de uma matrícula na rodada: <paramref name="Consulta"/> (sucesso) ou <paramref name="Erro"/> (mensagem sem credenciais).</summary>
public sealed record LgFeriasResultado(LgFeriasConsulta? Consulta, string? Erro);

/// <summary>Resultado de várias matrículas. <paramref name="Interrompida"/>: a rodada parou por falhas seguidas (<paramref name="Motivo"/>).</summary>
public sealed record LgFeriasRodada(IReadOnlyDictionary<(int Empresa, string Matricula), LgFeriasResultado> Resultados, bool Interrompida, string? Motivo);

/// <summary>
/// Cliente da operação ConsultarLista do ServicoDeFerias v2 da LG (uma chamada por contrato: empresa + matrícula). Mesmo
/// transporte das outras operações (<see cref="LgSoapCore"/>: retry com backoff em erro transitório, SOAP Fault,
/// CodigoDoErro/Mensagens/Tipo). Envelope e resposta NUNCA vão para o log.
/// </summary>
public sealed class LgFeriasClient(HttpClient http, IOptionsMonitor<LgOptions> options, ILogger<LgFeriasClient> logger)
{
    private readonly LgSoapCore _core = new(http, options, logger);

    /// <summary>Espera entre tentativas (substituível nos testes).</summary>
    public Func<TimeSpan, CancellationToken, Task> Esperar
    {
        get => _core.Esperar;
        set => _core.Esperar = value;
    }

    /// <summary>Operação com o namespace do filtro configurado (LG:Ferias:NamespaceFiltro; vazio = o do WSDL).</summary>
    public static LgOperacao Operacao(LgOptions o) =>
        string.IsNullOrWhiteSpace(o.Ferias.NamespaceFiltro) ? LgOperacao.Ferias : LgOperacao.Ferias with { NamespaceDto = o.Ferias.NamespaceFiltro.Trim() };

    // ---------- Envelope ----------

    /// <summary>
    /// ConsultarLista/filtro/Colaborador (IdentificacaoDeContrato): Empresa/Codigo e Matricula, na ordem do WSDL
    /// (DataContract: Empresa antes de Matricula).
    /// </summary>
    public static string MontarEnvelope(LgOptions o, int empresa, string matricula)
    {
        var op = Operacao(o);
        XNamespace dto = op.NamespaceDto;
        var filtro = new XElement(dto + "Colaborador",
            new XElement(dto + "Empresa", new XElement(dto + "Codigo", empresa)),
            new XElement(dto + "Matricula", matricula));
        return LgXml.MontarEnvelope(o, op, [filtro]);
    }

    // ---------- Resposta ----------

    /// <summary>
    /// Lê os FeriasParcial de ConsultarListaResult (Retorno vazio/nil = sem férias). SOAP Fault, CodigoDoErro/Mensagens
    /// ou Tipo ≠ 0 viram <see cref="LgIntegracaoException"/> com a mensagem da LG.
    /// </summary>
    public static LgFeriasConsulta LerResposta(string xml)
    {
        var pagina = LgXml.LerPagina(xml, LgOperacao.Ferias, "FeriasParcial", f =>
            (Inicio: LgXml.Data(LgXml.Filho(f, "DataDaOcorrencia")), Fim: LgXml.Data(LgXml.Filho(f, "DataFim"))));
        var periodos = pagina.Itens
            .Where(p => p.Inicio is not null && p.Fim is not null && p.Fim >= p.Inicio)
            .Select(p => new LgFeriasPeriodo(p.Inicio!.Value, p.Fim!.Value))
            .OrderBy(p => p.Inicio)
            .ToList();
        return new LgFeriasConsulta(periodos, pagina.Itens.Count - periodos.Count);
    }

    // ---------- HTTP ----------

    /// <summary>Férias de um contrato, com retry curto em erro transitório.</summary>
    public Task<LgFeriasConsulta> ConsultarAsync(int empresa, string matricula, CancellationToken ct)
    {
        var o = options.CurrentValue;
        return _core.ConsultarPaginaAsync(Operacao(o), 1, atual => MontarEnvelope(atual, empresa, matricula), LerResposta, ct);
    }

    /// <summary>
    /// Várias matrículas com no máximo <paramref name="paralelismo"/> chamadas simultâneas. A falha de uma não interrompe
    /// as outras (vira <see cref="LgFeriasResultado.Erro"/>); <paramref name="falhasParaInterromper"/> falhas SEGUIDAS
    /// (LG fora do ar, configuração recusada) param a rodada e as matrículas restantes ficam como não consultadas.
    /// <paramref name="progresso"/>(feitas, total).
    /// </summary>
    public async Task<LgFeriasRodada> ConsultarVariosAsync(IReadOnlyList<(int Empresa, string Matricula)> chaves, int paralelismo,
        int falhasParaInterromper, Action<int, int>? progresso, CancellationToken ct)
    {
        var resultados = new ConcurrentDictionary<(int, string), LgFeriasResultado>();
        using var interromper = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var feitas = 0;
        var seguidas = 0;
        string? motivo = null;
        var limite = Math.Max(1, falhasParaInterromper);
        try
        {
            await Parallel.ForEachAsync(chaves,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, paralelismo), CancellationToken = interromper.Token },
                async (chave, token) =>
                {
                    try
                    {
                        resultados[chave] = new LgFeriasResultado(await ConsultarAsync(chave.Empresa, chave.Matricula, token), null);
                        Interlocked.Exchange(ref seguidas, 0);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        var mensagem = ex is LgIntegracaoException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";
                        resultados[chave] = new LgFeriasResultado(null, mensagem);
                        if (Interlocked.Increment(ref seguidas) >= limite && Interlocked.CompareExchange(ref motivo, mensagem, null) is null)
                            await interromper.CancelAsync();
                    }
                    progresso?.Invoke(Interlocked.Increment(ref feitas), chaves.Count);
                });
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Interrompida pelas falhas seguidas: as não consultadas ficam como falha abaixo
        }

        var interrompida = motivo is not null;
        if (interrompida)
        {
            logger.LogWarning("LG: consulta de férias interrompida após {Falhas} falhas seguidas ({Motivo})", limite, motivo);
            foreach (var chave in chaves.Where(c => !resultados.ContainsKey(c)))
                resultados[chave] = new LgFeriasResultado(null, $"não consultada: rodada interrompida após {limite} falhas seguidas ({motivo})");
        }
        return new LgFeriasRodada(resultados, interrompida, motivo);
    }
}

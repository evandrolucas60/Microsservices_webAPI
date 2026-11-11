using System.Globalization;
using Employee.Api.Data;
using Escala5x2.BuildingBlocks.Contracts.Employees;
using Escala5x2.BuildingBlocks.Contracts.Http;
using Escala5x2.BuildingBlocks.Shared.Lg;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Employee.Api.Lg;

/// <summary>Quem consultar: colaboradores ativos com vínculo LG, opcionalmente só de uma loja, de um setor ou da lista.</summary>
public sealed record LgFeriasFiltro(Guid? StoreId = null, Guid? SectorId = null, IReadOnlyCollection<Guid>? EmployeeIds = null);

/// <summary>
/// Resumo de uma rodada de férias (sem nomes). <paramref name="Status"/>: <see cref="FeriasLgStatus"/>.
/// <paramref name="Consultados"/>: matrículas consultadas com sucesso; <paramref name="Falhas"/>: matrículas que falharam
/// (mantêm os períodos já gravados); <paramref name="Alterados"/>: colaboradores com algum período criado/ajustado/removido.
/// <paramref name="UltimaSincronizacao"/>: a consulta mais antiga entre os colaboradores (UTC).
/// </summary>
public sealed record LgFeriasResumo(
    string Status, DateTime IniciadaEm, DateTime ConcluidaEm, int Colaboradores, int Consultados, int Falhas, int Alterados,
    int Registradas, int Alteradas, int Removidas, int Invalidos, bool Interrompida, IReadOnlyList<string> Erros, string? Mensagem,
    DateTime? UltimaSincronizacao);

/// <summary>
/// Consulta na LG as férias dos colaboradores ativos (uma chamada por empresa + matrícula, com paralelismo limitado) e grava
/// como ausências "Férias" de origem LG (<see cref="LgFeriasRegras"/>). Fases: (1) lê quem consultar; (2) consulta a LG
/// sem segurar o banco; (3) aplica e grava numa seção exclusiva do processo (sincronização completa, botão do colaborador e
/// atualização antes da geração não se atropelam). Falha de uma matrícula não interrompe as outras; publica
/// <see cref="EmployeeUnavailabilityChanged"/> por período alterado (o Scheduling.Api reavalia as escalas editáveis e a
/// auditoria grava).
/// </summary>
public sealed class LgFeriasSincronizador(
    EmployeeDbContext db, LgFeriasClient lg, IPublishEndpoint bus, IOptionsMonitor<LgOptions> options, ILogger<LgFeriasSincronizador> logger)
{
    private const int MaxErros = 50;

    /// <summary>Seção exclusiva da gravação (um Employee.Api por banco).</summary>
    private static readonly SemaphoreSlim Gravacao = new(1, 1);

    /// <param name="forcar">false = pula a consulta se todos foram consultados há menos de LG:Ferias:ValidadeMinutos.</param>
    public async Task<LgFeriasResumo> SincronizarAsync(LgFeriasFiltro filtro, string por, Action<string, int, int>? progresso, CancellationToken ct,
        bool forcar = true)
    {
        var o = options.CurrentValue;
        var inicio = DateTime.UtcNow;
        var op = LgFeriasClient.Operacao(o);
        if (!o.ConfiguradaPara(op))
            return Resumo(FeriasLgStatus.NaoConfigurada, inicio, 0, 0, 0, 0, 0, 0, 0, 0, false, [], o.MensagemNaoConfigurada(op), null);

        // (1) Quem consultar: ativos com vínculo LG
        var query = db.Employees.AsNoTracking().Where(e => e.Active && e.LgEmpresaCodigo != null);
        if (filtro.StoreId is { } loja) query = query.Where(e => e.StoreId == loja);
        if (filtro.SectorId is { } setor) query = query.Where(e => e.Sectors.Any(s => s.SectorId == setor));
        if (filtro.EmployeeIds is { Count: > 0 } ids)
        {
            var lista = ids.ToList();
            query = query.Where(e => lista.Contains(e.Id));
        }
        var alvos = await query.Select(e => new { e.Id, Empresa = e.LgEmpresaCodigo!.Value, e.Registration, e.LgFeriasSincronizadoEm }).ToListAsync(ct);
        var ultima = alvos.Count == 0 || alvos.Any(a => a.LgFeriasSincronizadoEm is null) ? null : alvos.Min(a => a.LgFeriasSincronizadoEm);
        if (alvos.Count == 0)
            return Resumo(FeriasLgStatus.Atualizada, inicio, 0, 0, 0, 0, 0, 0, 0, 0, false, [], null, null);
        if (!forcar && o.Ferias.ValidadeMinutos > 0 && ultima is { } u && u >= inicio.AddMinutes(-o.Ferias.ValidadeMinutos))
            return Resumo(FeriasLgStatus.Recente, inicio, alvos.Count, 0, 0, 0, 0, 0, 0, 0, false, [], null, ultima);

        // (2) LG, sem segurar o banco
        var chaves = alvos.Select(a => (a.Empresa, Matricula: a.Registration.Trim())).Distinct().ToList();
        progresso?.Invoke("Consultando as férias na LG", 0, chaves.Count);
        var rodada = await lg.ConsultarVariosAsync(chaves, o.Ferias.ParalelismoEfetivo, o.Ferias.FalhasSeguidasParaInterromper,
            (feitas, total) => progresso?.Invoke("Consultando as férias na LG", feitas, total), ct);

        // (3) Aplica e grava
        progresso?.Invoke("Gravando as férias", chaves.Count, chaves.Count);
        var hoje = o.Hoje(inicio);
        var agora = DateTime.UtcNow;
        var erros = new List<string>();
        var eventos = new List<EmployeeUnavailabilityChanged>();
        int consultados = 0, falhas = 0, alterados = 0, registradas = 0, alteradas = 0, removidas = 0;
        var invalidos = rodada.Resultados.Values.Sum(r => r.Consulta?.Invalidos ?? 0);
        await Gravacao.WaitAsync(ct);
        try
        {
            var idsAlvo = alvos.Select(a => a.Id).ToList();
            var colaboradores = await db.Employees.Include(e => e.Unavailabilities)
                .Where(e => idsAlvo.Contains(e.Id)).ToListAsync(ct);
            foreach (var e in colaboradores)
            {
                var chave = (e.LgEmpresaCodigo ?? 0, e.Registration.Trim());
                if (!rodada.Resultados.TryGetValue(chave, out var r) || r.Consulta is null)
                {
                    falhas++;
                    Adicionar(erros, $"Matrícula {e.Registration}: {r?.Erro ?? "não consultada"}");
                    continue;
                }
                consultados++;
                var aplicacao = LgFeriasRegras.Aplicar(e, r.Consulta.Periodos, hoje, o.Ferias.MesesPassados, agora);
                if (aplicacao.Novas.Count > 0) db.Unavailabilities.AddRange(aplicacao.Novas);
                if (aplicacao.Removidas.Count > 0) db.Unavailabilities.RemoveRange(aplicacao.Removidas);
                e.LgFeriasSincronizadoEm = agora;
                if (!aplicacao.Mudou) continue;
                alterados++;
                foreach (var m in aplicacao.Mudancas)
                {
                    if (m.Acao == "registrada") registradas++;
                    else if (m.Acao == "alterada") alteradas++;
                    else removidas++;
                    eventos.Add(new EmployeeUnavailabilityChanged(m.Ausencia.Id, e.Id, e.StoreId, m.Acao, m.Ausencia.Start, m.Ausencia.End,
                        m.Ausencia.Type.ToString(), m.InicioAnterior, m.FimAnterior, LgFeriasRegras.Autor, agora));
                }
            }
            await db.SaveChangesAsync(ct);
        }
        finally
        {
            Gravacao.Release();
        }

        await PublicarAsync(eventos, ct);
        var status = falhas == 0 ? FeriasLgStatus.Atualizada : consultados == 0 ? FeriasLgStatus.Falha : FeriasLgStatus.Parcial;
        var mensagem = rodada.Interrompida
            ? $"Consulta de férias interrompida após {o.Ferias.FalhasSeguidasParaInterromper} falhas seguidas: {rodada.Motivo}"
            : falhas > 0 && consultados == 0 ? erros.FirstOrDefault() : null;
        // Ainda falhou para alguém: a mais antiga continua a de antes
        var ultimaDepois = falhas == 0 ? agora : ultima;
        logger.LogInformation(
            "LG: férias consultadas por {Por}: {Colaboradores} colaborador(es), {Consultados} consultado(s), {Falhas} falha(s), {Registradas} período(s) registrado(s), {Alteradas} alterado(s), {Removidas} removido(s){Interrompida}",
            por, alvos.Count, consultados, falhas, registradas, alteradas, removidas, rodada.Interrompida ? " (interrompida)" : "");
        return Resumo(status, inicio, alvos.Count, consultados, falhas, alterados, registradas, alteradas, removidas, invalidos,
            rodada.Interrompida, erros, mensagem, ultimaDepois);
    }

    /// <summary>
    /// Publica os eventos sem derrubar a rodada; RabbitMQ fora do ar: avisa uma vez e para (não espera 15 s por evento). As
    /// escalas editáveis também leem as ausências atuais ao abrir/gerar.
    /// </summary>
    private async Task PublicarAsync(IReadOnlyList<EmployeeUnavailabilityChanged> eventos, CancellationToken ct)
    {
        for (var i = 0; i < eventos.Count; i++)
        {
            try
            {
                using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
                limite.CancelAfter(TimeSpan.FromSeconds(15));
                await bus.Publish(eventos[i], limite.Token);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning("LG: {Pendentes} evento(s) de férias não publicado(s) ({Motivo})", eventos.Count - i, ex.Message);
                return;
            }
        }
    }

    private static LgFeriasResumo Resumo(string status, DateTime inicio, int colaboradores, int consultados, int falhas, int alterados,
        int registradas, int alteradas, int removidas, int invalidos, bool interrompida, IReadOnlyList<string> erros, string? mensagem,
        DateTime? ultima) =>
        new(status, inicio, DateTime.UtcNow, colaboradores, consultados, falhas, alterados, registradas, alteradas, removidas, invalidos,
            interrompida, erros, mensagem, ultima);

    private static void Adicionar(List<string> lista, string texto)
    {
        if (lista.Count < MaxErros) lista.Add(texto);
        else if (lista.Count == MaxErros) lista.Add("... (lista truncada)");
    }
}

/// <summary>
/// Atualização das férias para a geração de escala (POST /internal/planning/ferias-lg): roda num escopo próprio, espera no
/// máximo o tempo pedido e, se a LG demorar, devolve "EmAndamento" e deixa terminar em segundo plano (a próxima geração já
/// encontra os períodos gravados). Nunca lança: a geração não pode ser bloqueada pela LG.
/// </summary>
public sealed class LgFeriasAtualizador(
    IServiceScopeFactory scopes, IOptionsMonitor<LgOptions> options, IHostApplicationLifetime lifetime, ILogger<LgFeriasAtualizador> logger)
{
    public const string Autor = "Geração de escala";

    public async Task<FeriasLgAtualizacaoResponse> AtualizarAsync(LgFeriasFiltro filtro, TimeSpan espera, CancellationToken ct)
    {
        var o = options.CurrentValue;
        if (!o.ConfiguradaPara(LgFeriasClient.Operacao(o)))
            return new FeriasLgAtualizacaoResponse(FeriasLgStatus.NaoConfigurada, 0, 0, 0, 0, null, null, o.MensagemNaoConfigurada(LgFeriasClient.Operacao(o)));

        var tarefa = Task.Run(async () =>
        {
            await using var scope = scopes.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<LgFeriasSincronizador>()
                .SincronizarAsync(filtro, Autor, null, lifetime.ApplicationStopping, forcar: false);
        }, CancellationToken.None);

        try
        {
            if (await Task.WhenAny(tarefa, Task.Delay(espera, ct)) == tarefa)
            {
                var r = await tarefa;
                return new FeriasLgAtualizacaoResponse(r.Status, r.Colaboradores, r.Consultados, r.Falhas, r.Alterados,
                    r.UltimaSincronizacao, Texto(o, r.UltimaSincronizacao), r.Mensagem);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("LG: férias não atualizadas para a geração ({Motivo})", $"{ex.GetType().Name}: {ex.Message}");
            return new FeriasLgAtualizacaoResponse(FeriasLgStatus.Falha, 0, 0, 0, 0, null, await UltimaTextoAsync(o, filtro), ex.Message);
        }

        // Demorou: segue em segundo plano (erros só no log)
        _ = tarefa.ContinueWith(t => logger.LogWarning("LG: atualização de férias em segundo plano falhou ({Motivo})", t.Exception?.GetBaseException().Message),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        return new FeriasLgAtualizacaoResponse(FeriasLgStatus.EmAndamento, 0, 0, 0, 0, null, await UltimaTextoAsync(o, filtro),
            $"A LG não respondeu em {espera.TotalSeconds:0} s; a atualização continua em segundo plano.");
    }

    /// <summary>"dd/MM HH:mm" da consulta mais antiga dos colaboradores do filtro (null = algum nunca consultado).</summary>
    private async Task<string?> UltimaTextoAsync(LgOptions o, LgFeriasFiltro filtro)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<EmployeeDbContext>();
            var query = db.Employees.AsNoTracking().Where(e => e.Active && e.LgEmpresaCodigo != null);
            if (filtro.StoreId is { } loja) query = query.Where(e => e.StoreId == loja);
            if (filtro.SectorId is { } setor) query = query.Where(e => e.Sectors.Any(s => s.SectorId == setor));
            if (filtro.EmployeeIds is { Count: > 0 } ids)
            {
                var lista = ids.ToList();
                query = query.Where(e => lista.Contains(e.Id));
            }
            var datas = await query.Select(e => e.LgFeriasSincronizadoEm).ToListAsync();
            return datas.Count == 0 || datas.Any(d => d is null) ? null : Texto(o, datas.Min());
        }
        catch (Exception ex)
        {
            logger.LogWarning("LG: data da última consulta de férias indisponível ({Motivo})", ex.Message);
            return null;
        }
    }

    public static string? Texto(LgOptions o, DateTime? utc) =>
        utc is { } u ? TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(u, DateTimeKind.Utc), o.Fuso()).ToString("dd/MM HH:mm", CultureInfo.InvariantCulture) : null;
}

using Escala5x2.BuildingBlocks.Contracts.Stores;
using Escala5x2.BuildingBlocks.Shared.Lg;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Store.Api.Data;

namespace Store.Api.Lg;

/// <summary>
/// Sincroniza as lojas com os centros de custo da LG (regras em <see cref="LgLojasRegras"/>). A consulta inteira (todas
/// as páginas de todas as empresas) vem antes de qualquer alteração: falha na LG, XML inválido ou padrão inválido →
/// nada é gravado. Nunca apaga loja.
/// </summary>
public sealed class LgLojasSincronizador(
    StoreDbContext db, LgCentrosDeCustoClient lg, IPublishEndpoint bus, IOptionsMonitor<LgOptions> options, RolesLojasIntranet roles,
    ILogger<LgLojasSincronizador> logger)
{
    public async Task<LgLojasResumo> SincronizarAsync(string por, Action<string, int, int>? progresso, CancellationToken ct)
    {
        var o = options.CurrentValue;
        var inicio = DateTime.UtcNow;
        if (!o.ConfiguradaPara(LgOperacao.CentrosDeCusto)) throw new LgIntegracaoException(o.MensagemNaoConfigurada(LgOperacao.CentrosDeCusto));
        LgLojasRegras.Padrao(o.Lojas.PadraoEfetivo); // padrão inválido: falha antes de chamar a LG

        var consulta = await lg.ConsultarTodosAsync(progresso, ct);

        progresso?.Invoke("Gravando as lojas", consulta.Paginas, consulta.Paginas);
        var agora = DateTime.UtcNow;
        LgLojasResultado r;
        if (consulta.Centros.Count == 0)
        {
            r = new LgLojasResultado();
            r.Avisos.Add("A LG não devolveu nenhum centro de custo: nada foi alterado (confira LG:Empresas).");
        }
        else
        {
            var lojas = await db.Stores.ToListAsync(ct);
            r = LgLojasRegras.Aplicar(consulta.Centros, lojas, o.Lojas, o.Hoje(agora), por, agora);
            db.Stores.AddRange(r.Novas);
            await db.SaveChangesAsync(ct);
            // Roles por loja na intranet ("Escala5x2-Loja-{code}") de todas as lojas (idempotente no AuthAPI; não apaga
            // a role de loja inativada)
            await roles.PublicarAsync(lojas.Select(l => l.Code).Concat(r.Novas.Select(l => l.Code)), $"sincronização de lojas com a LG ({por})", ct);
        }

        var resumo = new LgLojasResumo("Concluida", inicio, DateTime.UtcNow, por, consulta.Empresas, consulta.Paginas, consulta.TotalLg,
            r.Lojas, r.Criadas, r.Atualizadas, r.Inalteradas, r.Inativadas, r.Reativadas, r.Ignorados.Count,
            r.Alteracoes, r.Ignorados, r.Conflitos, r.Avisos, null);
        logger.LogInformation(
            "LG: lojas sincronizadas por {Por}: {Total} centro(s) de custo, {Lojas} loja(s), {Criadas} criada(s), {Atualizadas} atualizada(s), {Inativadas} inativada(s), {Reativadas} reativada(s), {Ignorados} ignorado(s), {Conflitos} conflito(s)",
            por, consulta.TotalLg, r.Lojas, r.Criadas, r.Atualizadas, r.Inativadas, r.Reativadas, r.Ignorados.Count, r.Conflitos.Count);

        foreach (var ev in r.Eventos) await PublicarAsync(ev, ct);
        await PublicarAsync(Evento(resumo), ct);
        return resumo;
    }

    /// <summary>Evento de auditoria do resumo (contagens, centros de custo ignorados e conflitos).</summary>
    public static StoresLgSyncCompleted Evento(LgLojasResumo r) =>
        new(r.Status, r.Empresas, r.Paginas, r.TotalLg, r.Lojas, r.Criadas, r.Atualizadas, r.Inalteradas, r.Inativadas, r.Reativadas,
            r.Ignorados, r.CentrosIgnorados.Select(c => $"{c.Codigo} {c.Descricao} ({c.Motivo})").ToList(),
            r.Conflitos.Select(c => c.Mensagem).ToList(), r.Mensagem, r.Por, r.IniciadaEm, r.ConcluidaEm);

    /// <summary>Publica sem derrubar a sincronização (RabbitMQ fora do ar: só aviso no log).</summary>
    public async Task PublicarAsync<T>(T evento, CancellationToken ct) where T : class
    {
        try
        {
            using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limite.CancelAfter(TimeSpan.FromSeconds(15));
            await bus.Publish(evento, limite.Token);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("LG: evento {Tipo} não publicado ({Motivo})", typeof(T).Name, ex.Message);
        }
    }
}

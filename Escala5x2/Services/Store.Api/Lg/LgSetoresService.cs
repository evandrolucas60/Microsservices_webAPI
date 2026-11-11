using Escala5x2.BuildingBlocks.Contracts.Stores;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Store.Api.Data;
using Store.Api.Domain;

namespace Store.Api.Lg;

/// <summary>
/// Grava os setores da LG numa loja (regras em <see cref="LgSetoresRegras"/> e <see cref="LgSetoresCatalogoRegras"/>), com
/// repetição em caso de corrida (índice único loja + LgOrgaoCodigo). Publica <see cref="SectorLgChanged"/> por setor
/// criado/adotado/renomeado/inativado/reativado e um <see cref="StorePlanningChanged"/> com o resumo da loja.
/// Usado pelo garantir-lg do Employee.Api (órgãos dos colaboradores) e pela sincronização do catálogo de órgãos.
/// </summary>
public sealed class LgSetoresService(StoreDbContext db, IPublishEndpoint bus, ILogger<LgSetoresService> logger)
{
    /// <summary>null = loja não existe.</summary>
    public async Task<GarantirSetoresLgResponse?> GarantirAsync(Guid storeId, GarantirSetoresLgRequest req, CancellationToken ct)
    {
        if (!await db.Stores.AnyAsync(s => s.Id == storeId, ct)) return null;
        var por = string.IsNullOrWhiteSpace(req.Por) ? "Integração LG" : req.Por.Trim();
        var agora = DateTime.UtcNow;
        var orgaos = req.Orgaos ?? [];

        // Órgãos dos colaboradores: só cria/adota/renomeia; ativo/inativo é decisão do catálogo de órgãos
        var r = await AplicarAsync(storeId, setores => LgSetoresRegras.Garantir(orgaos, setores, storeId, agora), por,
            "Setores da LG (órgão responsável)", agora, ct);
        return new GarantirSetoresLgResponse(storeId, r.Mapa, r.Criados, r.Adotados, r.Renomeados, r.Detalhes, r.Avisos);
    }

    /// <summary>
    /// Aplica <paramref name="aplicar"/> nos setores (rastreados) da loja, grava com repetição e publica a auditoria.
    /// <paramref name="titulo"/>: início da mensagem do StorePlanningChanged.
    /// </summary>
    public async Task<LgSetoresResultado> AplicarAsync(Guid storeId, Func<List<Sector>, LgSetoresResultado> aplicar, string por,
        string titulo, DateTime agora, CancellationToken ct)
    {
        var r = await LgSetoresRegras.AplicarComRepeticaoAsync(aplicar,
            token => db.Sectors.Where(s => s.StoreId == storeId).ToListAsync(token),
            async (resultado, token) =>
            {
                try
                {
                    db.Sectors.AddRange(resultado.Novos);
                    await db.SaveChangesAsync(token);
                }
                catch
                {
                    db.ChangeTracker.Clear();
                    throw;
                }
            },
            ViolacaoUnica, ct);

        if (r.Alteracoes.Count > 0)
        {
            logger.LogInformation(
                "LG: setores da loja {StoreId} por {Por}: {Criados} criado(s), {Adotados} adotado(s), {Renomeados} renomeado(s), {Inativados} inativado(s), {Reativados} reativado(s)",
                storeId, por, r.Criados, r.Adotados, r.Renomeados, r.Inativados, r.Reativados);
            foreach (var (setor, acao, anterior) in r.Alteracoes)
                await PublicarAsync(new SectorLgChanged(storeId, setor.Id, setor.Name, setor.LgOrgaoCodigo ?? 0, setor.LgOrgaoDescricao, acao,
                    anterior, por, agora), ct);
            await PublicarAsync(new StorePlanningChanged(storeId, $"{titulo}: {Resumo(r)}", por, agora), ct);
        }
        return r;
    }

    /// <summary>"2 criado(s), 1 inativado(s) — FRIOS (criado), ..." (só as ações que aconteceram).</summary>
    public static string Resumo(LgSetoresResultado r)
    {
        var partes = new[]
            {
                (r.Criados, "criado(s)"), (r.Adotados, "adotado(s)"), (r.Renomeados, "renomeado(s)"),
                (r.Inativados, "inativado(s)"), (r.Reativados, "reativado(s)")
            }
            .Where(p => p.Item1 > 0).Select(p => $"{p.Item1} {p.Item2}");
        var nomes = string.Join(", ", r.Alteracoes.Select(a => $"{a.Setor.Name} ({a.Acao})"));
        return $"{string.Join(", ", partes)} — {nomes}";
    }

    /// <summary>Violação de índice único no PostgreSQL (23505).</summary>
    public static bool ViolacaoUnica(Exception ex) =>
        ex is DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } };

    /// <summary>Publica sem derrubar o pedido (RabbitMQ fora do ar: só aviso no log).</summary>
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

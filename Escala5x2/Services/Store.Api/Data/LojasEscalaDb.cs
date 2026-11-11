using Escala5x2.BuildingBlocks.Shared.Auth;
using Microsoft.EntityFrameworkCore;

namespace Store.Api.Data;

/// <summary>
/// No Store.Api a loja do perfil da intranet é casada direto no banco (sem chamar o próprio /internal/stores
/// e sem cache: loja recém-cadastrada já vale para o próximo usuário que tiver o cache dele vencido).
/// Banco indisponível → null (o usuário segue sem loja por 30 s), sem derrubar a autenticação.
/// </summary>
public sealed class LojasEscalaDb(StoreDbContext db, ILogger<LojasEscalaDb> logger) : ILojasEscala
{
    public async Task<IReadOnlyList<LojaEscala>?> ObterLojasAsync(CancellationToken ct = default)
    {
        try
        {
            return await db.Stores.AsNoTracking().OrderBy(s => s.Code)
                .Select(s => new LojaEscala(s.Id, s.Code, s.Name, s.LgCentroDeCustoCodigo, s.Active)).ToListAsync(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Não foi possível consultar as lojas no banco do Store.Api ({Motivo})", $"{ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }
}

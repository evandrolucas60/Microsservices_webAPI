using Escala5x2.BuildingBlocks.Shared.Auth;
using Microsoft.EntityFrameworkCore;
using Store.Api.Domain;

namespace Store.Api.Data;

/// <summary>
/// Cadastra na inicialização (depois das migrations) as lojas da lista oficial da intranet (<see cref="LojasRede"/>)
/// que ainda não existem no Store.Api. Idempotente: compara pelo código normalizado (<see cref="CodigoLoja.Normalizar"/>),
/// não altera nem apaga lojas existentes (a DEMO e as cadastradas à mão ficam como estão) e não cria setores,
/// turnos, demanda nem operação (cada loja configura os seus). Cidade fica vazia (só feriados nacionais/estaduais
/// até o Admin informar o município em "Editar loja"); UF PE; formato Varejo (editável).
/// Falha (banco fora, outra instância cadastrando ao mesmo tempo) só gera aviso: tenta de novo na próxima inicialização.
/// </summary>
public static class LojasRedeSeed
{
    public static async Task CadastrarLojasDaRedeAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StoreDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("LojasRede");
        try
        {
            var codigos = await db.Stores.AsNoTracking().Select(s => s.Code).ToListAsync(ct);
            var faltantes = LojasRede.Faltantes(codigos);
            if (faltantes.Count == 0) return;
            foreach (var loja in faltantes)
                db.Stores.Add(new Domain.Store { Code = loja.Code, Name = loja.Name, City = string.Empty, State = "PE", Format = StoreFormat.Varejo });
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Lojas da rede cadastradas a partir da lista da intranet: {Lojas}",
                string.Join(", ", faltantes.Select(l => $"{l.Code} - {l.Name}")));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Não foi possível cadastrar as lojas da rede ({Motivo}); nova tentativa na próxima inicialização",
                $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}

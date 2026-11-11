using BuildingBlocks.Contracts.Administracao;
using Escala5x2.BuildingBlocks.Shared.Auth;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace Store.Api.Data;

/// <summary>Role de loja da intranet (GET /api/stores/roles): nome pela convenção e a loja correspondente.</summary>
public sealed record RoleLojaResponse(string Role, Guid StoreId, string Code, string Name, bool Active);

/// <summary>Resultado do "Garantir roles das lojas" (POST /api/stores/garantir-roles).</summary>
public sealed record GarantirRolesLojasResponse(
    string Modelo, int Lojas, IReadOnlyList<string> Criadas, IReadOnlyList<string> Existentes, IReadOnlyList<string> Recusadas,
    IReadOnlyList<string> LojasSemRole);

/// <summary>
/// Roles por loja na intranet ("Escala5x2-Loja-{code}", <see cref="ModeloRoleLoja"/>): o AuthAPI cria as que faltam ao
/// receber <see cref="GarantirRoles"/> (idempotente; nunca apaga — role de loja inativa continua existindo). Publicado
/// sem esperar resposta quando uma loja é criada (POST /api/stores), depois de cada sincronização de lojas com a LG e na
/// inicialização do serviço; enviado como requisição/resposta pelo "Garantir roles das lojas" (Admin).
/// RabbitMQ/AuthAPI fora do ar: só aviso no log (o "Garantir roles das lojas" refaz tudo).
/// </summary>
public sealed class RolesLojasIntranet(
    StoreDbContext db, IPublishEndpoint bus, IRequestClient<GarantirRoles> client, IConfiguration configuration,
    ILogger<RolesLojasIntranet> logger)
{
    public const string Origem = "Escala5x2 Store.Api";
    private static readonly TimeSpan LimitePublicacao = TimeSpan.FromSeconds(15);

    public ModeloRoleLoja Modelo { get; } = ModeloRoleLoja.From(configuration);

    /// <summary>Roles de todas as lojas cadastradas (inclusive inativas), na ordem do código.</summary>
    public async Task<List<RoleLojaResponse>> ListarAsync(CancellationToken ct)
    {
        var lojas = await db.Stores.AsNoTracking().OrderBy(s => s.Code).Select(s => new { s.Id, s.Code, s.Name, s.Active }).ToListAsync(ct);
        return lojas
            .Select(l => Modelo.NomeDaRole(l.Code) is { } role ? new RoleLojaResponse(role, l.Id, l.Code, l.Name, l.Active) : null)
            .OfType<RoleLojaResponse>()
            .ToList();
    }

    /// <summary>Pede ao AuthAPI as roles das lojas informadas (pelo código), sem esperar resposta. Nunca lança (só avisa).</summary>
    public async Task PublicarAsync(IEnumerable<string> codigos, string motivo, CancellationToken ct = default)
    {
        var nomes = codigos.Select(Modelo.NomeDaRole).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (nomes.Length == 0) return;
        try
        {
            using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limite.CancelAfter(LimitePublicacao);
            await bus.Publish(new GarantirRoles { Nomes = nomes, Origem = $"{Origem}: {motivo}" }, limite.Token);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Roles de loja não enviadas ao AuthAPI ({Motivo}): {Roles}. Use \"Garantir roles das lojas\" depois.",
                ex.Message, string.Join(", ", nomes));
        }
    }

    /// <summary>Todas as lojas de uma vez, aguardando a resposta do AuthAPI; null = AuthAPI/RabbitMQ não respondeu.</summary>
    public async Task<GarantirRolesLojasResponse?> GarantirTodasAsync(string por, CancellationToken ct)
    {
        var lojas = await db.Stores.AsNoTracking().OrderBy(s => s.Code).Select(s => s.Code).ToListAsync(ct);
        var semRole = lojas.Where(c => Modelo.NomeDaRole(c) is null).ToList();
        var nomes = lojas.Select(Modelo.NomeDaRole).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        RolesGarantidas resposta;
        try
        {
            resposta = nomes.Length == 0
                ? new RolesGarantidas()
                : (await client.GetResponse<RolesGarantidas>(new GarantirRoles { Nomes = nomes, Origem = $"{Origem}: garantir roles das lojas ({por})" },
                    ct, RequestTimeout.After(s: 20))).Message;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("AuthAPI não respondeu ao \"Garantir roles das lojas\" ({Motivo})", $"{ex.GetType().Name}: {ex.Message}");
            return null;
        }
        logger.LogInformation("Roles de loja garantidas por {Por}: {Criadas} criada(s), {Existentes} já existente(s), {Recusadas} recusada(s)",
            por, resposta.Criadas.Length, resposta.Existentes.Length, resposta.Recusadas.Length);
        return new GarantirRolesLojasResponse(Modelo.Modelo, lojas.Count, resposta.Criadas, resposta.Existentes, resposta.Recusadas, semRole);
    }
}

/// <summary>
/// Na inicialização (depois de 30 s, com o RabbitMQ já conectado), pede ao AuthAPI as roles de todas as lojas: cobre as
/// lojas cadastradas pela lista da intranet (seed), as criadas antes desta versão e o primeiro deploy.
/// Desligue com RolesLoja:GarantirNaInicializacao = false.
/// </summary>
public sealed class RolesLojasNaInicializacao(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<RolesLojasNaInicializacao> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("RolesLoja:GarantirNaInicializacao", true)) return;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<StoreDbContext>();
            var roles = scope.ServiceProvider.GetRequiredService<RolesLojasIntranet>();
            var codigos = await db.Stores.AsNoTracking().Select(s => s.Code).ToListAsync(stoppingToken);
            await roles.PublicarAsync(codigos, "inicialização", stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Serviço parando
        }
        catch (Exception ex)
        {
            logger.LogWarning("Roles de loja não garantidas na inicialização ({Motivo})", $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}

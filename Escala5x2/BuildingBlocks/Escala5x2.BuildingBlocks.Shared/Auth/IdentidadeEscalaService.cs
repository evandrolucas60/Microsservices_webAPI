using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using BuildingBlocks.Contracts.Identidade;
using BuildingBlocks.Shared.Security;
using Escala5x2.BuildingBlocks.Shared.Messaging;
using MassTransit;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Escala5x2.BuildingBlocks.Shared.Auth;

/// <summary>Identidade 5x2 do usuário da intranet (vira as claims sub/name/role/store e a resposta de GET /api/me).</summary>
public sealed record IdentidadeEscala
{
    /// <summary>Id do usuário na intranet (claim "id").</summary>
    public string UserId { get; init; } = string.Empty;
    /// <summary>Claim "sub": o id da intranet quando é Guid; senão um Guid derivado dele.</summary>
    public Guid Sub { get; init; }
    public string Name { get; init; } = string.Empty;
    /// <summary>Papel 5x2 efetivo (Colaborador com role de loja chega como Gestor; ver <see cref="EscopoLojas"/>).</summary>
    public string Role { get; init; } = Roles.Colaborador;
    /// <summary>Loja principal: a do perfil da intranet; sem ela, a primeira loja das roles de loja.</summary>
    public Guid? StoreId { get; init; }
    public string? StoreCode { get; init; }
    public string? StoreName { get; init; }
    public string? Loja { get; init; }
    /// <summary>Lojas permitidas: a do perfil ∪ as das roles "Escala5x2-Loja-{code}" do token (claims "escala_store").</summary>
    public IReadOnlyList<Guid> StoreIds { get; init; } = [];
    /// <summary>Lojas em que o usuário é Gestor (claims "escala_store_gestor").</summary>
    public IReadOnlyList<Guid> ManagedStoreIds { get; init; } = [];
}

/// <summary>Papel efetivo e lojas permitidas calculados por <see cref="IdentidadeEscalaService.CalcularAcesso"/>.</summary>
public sealed record AcessoLojas(string Role, Guid? StoreId, IReadOnlyList<Guid> StoreIds, IReadOnlyList<Guid> ManagedStoreIds);

/// <summary>
/// Calcula a identidade 5x2 a partir do JWT da intranet: papel pelas roles do token e lojas pelo perfil da
/// intranet (IdentidadeAPI via RabbitMQ, Work.Location) casada pelo código com as lojas do Store.Api, mais as
/// lojas das roles por loja ("Escala5x2-Loja-{code}", <see cref="ModeloRoleLoja"/>). O perfil fica em cache por
/// 5 minutos por usuário (30 s quando o IdentidadeAPI não respondeu); as lojas do Store.Api são resolvidas a cada
/// chamada. Roles novas só chegam com um token novo (novo login ou renovação pelo refresh token).
/// </summary>
public sealed class IdentidadeEscalaService(
    IRequestClient<ObterMembroPorUsuario> membroPorUsuario,
    ILojasEscala lojas,
    IMemoryCache cache,
    IConfiguration configuration,
    ILogger<IdentidadeEscalaService> logger)
{
    private static readonly TimeSpan Duracao = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DuracaoFalha = TimeSpan.FromSeconds(30);
    private const string ChaveIdentidadeIndisponivel = "escala5x2:identidade-indisponivel";

    // Espera máxima pelo IdentidadeAPI (RabbitMq:RequestTimeoutSeconds, padrão 5 s)
    private readonly RabbitMqOptions _rabbitMq = RabbitMqOptions.From(configuration);

    // Mesmos papéis de superAdmin do painel de gestão da intranet (Administracao:Roles; padrão superAdmin)
    private readonly string[] _superAdminRoles = AdminPolicies.Roles(configuration);

    // Convenção das roles por loja (RolesLoja:Modelo; padrão "Escala5x2-Loja-{code}")
    private readonly ModeloRoleLoja _modeloRoleLoja = ModeloRoleLoja.From(configuration);

    /// <summary>
    /// Lojas permitidas e papel efetivo (regra em <see cref="EscopoLojas"/>): lojas = a do perfil ∪ as das roles de
    /// loja do token cujo código (normalizado: "1" = "01") existe no Store.Api; Admin/RH/Jurídico mantêm o papel (escopo
    /// global); Gestor gerencia todas as permitidas; Colaborador com role de loja vira Gestor só nas lojas das roles
    /// (a do perfil continua só consulta); Colaborador sem role de loja fica só com a do perfil.
    /// Loja principal: a do perfil; sem ela, a primeira das roles (ordem do código).
    /// </summary>
    public static AcessoLojas CalcularAcesso(
        string papel, Guid? lojaPerfil, IEnumerable<string> rolesIntranet, IReadOnlyList<LojaEscala>? lojasCadastradas, ModeloRoleLoja modelo)
    {
        var codigos = rolesIntranet.Select(modelo.CodigoDaRole).Select(CodigoLoja.Normalizar).OfType<string>().ToHashSet();
        var porRole = codigos.Count == 0 || lojasCadastradas is null
            ? []
            : lojasCadastradas
                .Where(l => CodigoLoja.Normalizar(l.Code) is { } n && codigos.Contains(n))
                .OrderBy(l => l.Code, OrdemCodigo.Instancia)
                .Select(l => l.Id)
                .Distinct()
                .ToList();

        var todas = new List<Guid>();
        if (lojaPerfil is { } perfil) todas.Add(perfil);
        todas.AddRange(porRole.Where(id => id != lojaPerfil));
        Guid? principal = lojaPerfil ?? (todas.Count > 0 ? todas[0] : null);

        if (EscopoLojas.Global(papel)) return new AcessoLojas(papel, principal, todas, []);
        if (papel == Roles.Gestor) return new AcessoLojas(Roles.Gestor, principal, todas, todas);
        if (porRole.Count > 0) return new AcessoLojas(Roles.Gestor, principal, todas, porRole);
        return new AcessoLojas(Roles.Colaborador, principal, todas, []);
    }

    /// <summary>Códigos numéricos em ordem numérica ("01", "2", ..., "30"), depois os demais ("CD", "DEMO").</summary>
    private sealed class OrdemCodigo : IComparer<string>
    {
        public static readonly OrdemCodigo Instancia = new();

        public int Compare(string? a, string? b)
        {
            var na = Numero(a);
            var nb = Numero(b);
            if (na is not null && nb is not null) return na != nb ? na.Value.CompareTo(nb.Value) : string.CompareOrdinal(a, b);
            if (na is not null) return -1;
            if (nb is not null) return 1;
            return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        }

        private static long? Numero(string? codigo)
        {
            var digitos = new string((codigo ?? string.Empty).Trim().TakeWhile(char.IsDigit).ToArray());
            return digitos.Length is > 0 and <= 18 ? long.Parse(digitos) : null;
        }
    }

    /// <summary>
    /// superAdmin → Admin; roles "RH", "Juridico" e "Gestor" da intranet → mesmo papel; demais → Colaborador.
    /// Com várias roles vale a de maior precedência: Admin > RH > Juridico > Gestor > Colaborador.
    /// Comparação sem diferenciar maiúsculas e sem espaços nas pontas (há "superAdmin " no banco).
    /// </summary>
    public static string MapearPapel(IEnumerable<string> rolesIntranet, IEnumerable<string> superAdminRoles)
    {
        var roles = rolesIntranet.Select(r => r.Trim()).Where(r => r.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (superAdminRoles.Any(r => roles.Contains(r.Trim()))) return Roles.Admin;
        if (roles.Contains(Roles.RH)) return Roles.RH;
        if (roles.Contains(Roles.Juridico) || roles.Contains("Jurídico")) return Roles.Juridico;
        if (roles.Contains(Roles.Gestor)) return Roles.Gestor;
        return Roles.Colaborador;
    }

    /// <summary>Guid estável a partir do id da intranet (os serviços 5x2 esperam "sub" Guid).</summary>
    public static Guid SubDoUsuario(string userId)
    {
        if (Guid.TryParse(userId, out var id)) return id;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"intranet-user:{userId}"));
        return new Guid(hash.AsSpan(0, 16));
    }

    /// <summary>Roles do JWT da intranet (claim "role"; ClaimTypes.Role quando o mapeamento de claims está ligado).</summary>
    public static IEnumerable<string> RolesIntranet(ClaimsPrincipal user) =>
        user.Claims.Where(c => c.Type == ClaimTypes.Role || c.Type == "role").Select(c => c.Value);

    /// <summary>
    /// Identidade do usuário do JWT da intranet; null se o token não tiver a claim "id".
    /// IdentidadeAPI/Store.Api fora do ar não geram erro (segue sem loja); só a requisição abortada
    /// pelo cliente (<paramref name="ct"/>) interrompe com OperationCanceledException.
    /// </summary>
    public async Task<IdentidadeEscala?> ObterAsync(ClaimsPrincipal user, CancellationToken ct = default)
    {
        var userId = user.FindFirst(EscalaClaims.IntranetId)?.Value;
        if (string.IsNullOrWhiteSpace(userId)) return null;

        var rolesIntranet = RolesIntranet(user).ToList();
        var papel = MapearPapel(rolesIntranet, _superAdminRoles);

        // Loja do perfil (IdentidadeAPI; só o perfil fica em cache) + lojas do Store.Api, consultadas a cada chamada
        // (loja recém-cadastrada vale na hora: o Store.Api lê do banco; os demais usam o cache do /internal/stores)
        // quando há loja no perfil ou role de loja no token
        var perfil = await ObterPerfilAsync(userId, ct);
        var codigo = CodigoLoja.DaLoja(perfil.Loja);
        var temRoleDeLoja = rolesIntranet.Any(r => _modeloRoleLoja.CodigoDaRole(r) is not null);
        var cadastradas = codigo is not null || temRoleDeLoja ? await lojas.ObterLojasAsync(ct) : null;
        var normalizado = CodigoLoja.Normalizar(codigo);
        var doPerfil = codigo is null ? null : cadastradas?.FirstOrDefault(l => CodigoLoja.Normalizar(l.Code) == normalizado);

        var acesso = CalcularAcesso(papel, doPerfil?.Id, rolesIntranet, cadastradas, _modeloRoleLoja);
        var principal = doPerfil ?? (acesso.StoreId is { } sid ? cadastradas?.FirstOrDefault(l => l.Id == sid) : null);

        // Nome do perfil da intranet; sem perfil, o login (unique_name no JWT do AuthAPI)
        var nomeToken = user.FindFirst("unique_name")?.Value ?? user.FindFirst(ClaimTypes.Name)?.Value ?? user.FindFirst("name")?.Value;
        return new IdentidadeEscala
        {
            UserId = userId,
            Sub = SubDoUsuario(userId),
            Name = perfil.Nome ?? nomeToken ?? "usuário",
            Role = acesso.Role,
            StoreId = acesso.StoreId,
            // Código do perfil mesmo sem loja correspondente (a tela avisa "loja informada"); senão o da loja principal
            StoreCode = doPerfil is null && principal is not null ? principal.Code : codigo,
            StoreName = principal?.Name,
            Loja = perfil.Loja,
            StoreIds = acesso.StoreIds,
            ManagedStoreIds = acesso.ManagedStoreIds,
        };
    }

    private sealed record PerfilUsuario(string? Nome, string? Loja);

    /// <summary>Nome e loja do perfil da intranet (cache por usuário: 5 min; 30 s quando o IdentidadeAPI não respondeu).</summary>
    private async Task<PerfilUsuario> ObterPerfilAsync(string userId, CancellationToken ct)
    {
        var cacheKey = $"escala5x2:perfil:{userId}";
        if (cache.TryGetValue(cacheKey, out PerfilUsuario? cached) && cached is not null) return cached;

        // IdentidadeAPI/RabbitMQ sem responder há pouco: nem tenta de novo (ninguém espera o timeout outra vez durante 30 s)
        var indisponivel = cache.TryGetValue(ChaveIdentidadeIndisponivel, out _) || cache.TryGetValue(RabbitMqOptions.ChaveSemConexao, out _);
        var membro = indisponivel ? null : await ConsultarMembroAsync(userId, ct);

        PerfilUsuario perfil;
        if (membro is null)
        {
            // IdentidadeAPI fora do ar: segue sem loja (Admin/RH/Jurídico continuam acessando tudo)
            perfil = new PerfilUsuario(null, null);
            cache.Set(cacheKey, perfil, DuracaoFalha);
            return perfil;
        }

        perfil = membro.Encontrado
            ? new PerfilUsuario(
                string.IsNullOrWhiteSpace(membro.Nome) ? null : membro.Nome,
                string.IsNullOrWhiteSpace(membro.Loja) ? null : membro.Loja.Trim())
            : new PerfilUsuario(null, null);
        cache.Set(cacheKey, perfil, Duracao);
        return perfil;
    }

    /// <summary>
    /// Perfil do usuário no IdentidadeAPI (RabbitMQ) com timeout curto (RabbitMq:RequestTimeoutSeconds, padrão 5 s);
    /// null quando não respondeu (timeout, RabbitMQ inacessível...). A falha é avisada uma vez e vale por 30 s.
    /// </summary>
    private async Task<MembroPorUsuarioResposta?> ConsultarMembroAsync(string userId, CancellationToken ct)
    {
        // Limite próprio além do RequestTimeout do MassTransit: vale mesmo com o bus ainda sem conexão com o
        // RabbitMQ (a requisição não depende só do RequestAborted do cliente/gateway)
        using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limite.CancelAfter(_rabbitMq.LimiteEspera);
        try
        {
            var resposta = await membroPorUsuario.GetResponse<MembroPorUsuarioResposta>(
                new ObterMembroPorUsuario { UserId = userId }, limite.Token, RequestTimeout.After(ms: (int)_rabbitMq.RequestTimeout.TotalMilliseconds));
            return resposta.Message;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            cache.Set(ChaveIdentidadeIndisponivel, true, DuracaoFalha);
            string motivo;
            if (ex is OperationCanceledException)
            {
                // Estourou o nosso limite: a requisição nem saiu (RabbitMQ sem conexão); o bloqueio de usuários
                // (RestricoesUsuarioEscala) também deixa de esperar
                cache.Set(RabbitMqOptions.ChaveSemConexao, true, DuracaoFalha);
                motivo = $"RabbitMQ sem conexão; sem resposta em {_rabbitMq.RequestTimeout.TotalSeconds:0} s";
            }
            else
            {
                motivo = ex is RequestTimeoutException ? $"sem resposta em {_rabbitMq.RequestTimeout.TotalSeconds:0} s" : $"{ex.GetType().Name}: {ex.Message}";
            }
            // Sem a pilha: é indisponibilidade de outro serviço, não erro deste
            logger.LogWarning(
                "IdentidadeAPI não respondeu à consulta da loja do usuário {UserId} ({Motivo}); seguindo sem loja pelos próximos {Segundos} s",
                userId, motivo, DuracaoFalha.TotalSeconds);
            return null;
        }
    }
}

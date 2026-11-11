using Microsoft.AspNetCore.Http;

namespace Escala5x2.BuildingBlocks.Shared.Auth;

/// <summary>
/// Usuário da requisição: JWT da intranet validado pelo próprio serviço, com as claims sub, name, role, store,
/// escala_store e escala_store_gestor calculadas pela <see cref="EscalaClaimsTransformation"/> (papel 5x2, loja do
/// perfil da intranet e lojas das roles "Escala5x2-Loja-{code}"). Papel efetivo por loja: <see cref="EscopoLojas"/>.
/// </summary>
public interface ICurrentUser
{
    Guid? UserId { get; }
    string Name { get; }
    /// <summary>Papel 5x2 (políticas). Colaborador com role de loja chega como Gestor: confira a loja com <see cref="RoleIn"/>.</summary>
    string? Role { get; }
    /// <summary>Loja principal (a do perfil; sem ela, a primeira das roles de loja).</summary>
    Guid? StoreId { get; }
    /// <summary>Lojas permitidas (perfil ∪ roles de loja). Admin, RH e Jurídico acessam todas, mesmo fora deste conjunto.</summary>
    IReadOnlySet<Guid> StoreIds { get; }
    /// <summary>Lojas em que o usuário é Gestor (visualizar e gerar/editar).</summary>
    IReadOnlySet<Guid> ManagedStoreIds { get; }
    /// <summary>Identificação usada em auditoria: "nome (id)".</summary>
    string Display { get; }
    /// <summary>Admin, RH e Jurídico enxergam todas as lojas; Gestor e Colaborador só as permitidas.</summary>
    bool HasGlobalScope { get; }
    /// <summary>Papel efetivo na loja (Admin/RH/Juridico: o próprio; Gestor; Colaborador); null = sem acesso à loja.</summary>
    string? RoleIn(Guid storeId);
    /// <summary>Se o usuário pode ver dados da loja (Gestor/Colaborador sem loja não acessam nenhuma).</summary>
    bool CanAccessStore(Guid storeId);
    /// <summary>Gerenciar a loja (gerar/editar/aprovar escala, setores, turnos, operação): Admin, ou Gestor da loja.</summary>
    bool CanManageStore(Guid storeId);
}

internal sealed class CurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private System.Security.Claims.ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public Guid? UserId => Guid.TryParse(Principal?.FindFirst("sub")?.Value, out var id) ? id : null;
    public string Name => Principal?.FindFirst("name")?.Value ?? "sistema";
    public string? Role => Principal?.FindFirst("role")?.Value;
    public Guid? StoreId => Guid.TryParse(Principal?.FindFirst(EscalaClaims.Store)?.Value, out var id) ? id : null;

    // Lidas das claims a cada uso (sem cache: o serviço pode ser resolvido antes da autenticação na mesma requisição).
    // A loja principal sempre conta como permitida (mesmo se a claim escala_store faltar)
    public IReadOnlySet<Guid> StoreIds => Lojas(EscalaClaims.Stores, StoreId);
    public IReadOnlySet<Guid> ManagedStoreIds => Lojas(EscalaClaims.StoresGestor, null);

    public string Display => UserId is null ? Name : $"{Name} ({UserId})";
    public bool HasGlobalScope => EscopoLojas.Global(Role);
    public string? RoleIn(Guid storeId) => EscopoLojas.PapelNaLoja(Role, StoreIds, ManagedStoreIds, storeId);
    public bool CanAccessStore(Guid storeId) => RoleIn(storeId) is not null;
    public bool CanManageStore(Guid storeId) => RoleIn(storeId) is Roles.Admin or Roles.Gestor;

    private HashSet<Guid> Lojas(string tipo, Guid? extra)
    {
        var set = new HashSet<Guid>();
        foreach (var c in Principal?.FindAll(tipo) ?? [])
            if (Guid.TryParse(c.Value, out var id)) set.Add(id);
        if (extra is { } e) set.Add(e);
        return set;
    }
}

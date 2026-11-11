namespace Escala5x2.BuildingBlocks.Shared.Auth;

/// <summary>
/// Papéis da Escala 5x2, calculados a partir das roles da intranet por <see cref="IdentidadeEscalaService"/>
/// (superAdmin → Admin; RH, Juridico e Gestor → mesmo papel; demais → Colaborador; Colaborador com role de loja
/// "Escala5x2-Loja-{code}" → Gestor só nessas lojas, ver <see cref="EscopoLojas"/>).
/// </summary>
public static class Roles
{
    public const string Admin = "Admin";
    public const string Gestor = "Gestor";
    public const string RH = "RH";
    public const string Juridico = "Juridico";
    public const string Colaborador = "Colaborador";

    public static readonly string[] All = [Admin, Gestor, RH, Juridico, Colaborador];
}

public static class Policies
{
    public const string Authenticated = "authenticated";
    public const string Admin = "admin";
    public const string Manager = "manager";
    public const string HumanResources = "hr";
    public const string Legal = "legal";
    public const string Audit = "audit";
    /// <summary>
    /// Sincronizações com a LG (colaboradores, lojas e setores): executar e ver o status/resumo — só superAdmin (Admin)
    /// e RH. Os resumos trazem centros de custo ignorados, matrículas etc. e não podem vazar para outros papéis.
    /// </summary>
    public const string LgSync = "lg-sync";
}

/// <summary>
/// Claims do usuário depois da <see cref="EscalaClaimsTransformation"/>. "id" é a claim original do JWT
/// da intranet; as demais são calculadas pelos serviços 5x2 e lidas pelo <see cref="ICurrentUser"/>.
/// </summary>
public static class EscalaClaims
{
    /// <summary>Id do usuário na intranet (claim "id" do JWT emitido pelo AuthAPI).</summary>
    public const string IntranetId = "id";
    /// <summary>Guid do usuário: o id da intranet quando já é Guid; senão um Guid derivado dele.</summary>
    public const string Sub = "sub";
    public const string Name = "name";
    /// <summary>Papel 5x2 (Admin, RH, Juridico, Gestor ou Colaborador).</summary>
    public const string Role = "role";
    /// <summary>
    /// Loja principal: StoreId da loja do perfil da intranet no Store.Api (quando houver correspondência); sem ela, a
    /// primeira loja das roles de loja.
    /// </summary>
    public const string Store = "store";
    /// <summary>Uma claim por loja permitida (perfil ∪ roles "Escala5x2-Loja-{code}"), incluindo a principal.</summary>
    public const string Stores = "escala_store";
    /// <summary>Uma claim por loja em que o usuário é Gestor (ver <see cref="EscopoLojas"/>).</summary>
    public const string StoresGestor = "escala_store_gestor";
    public const string StoreCode = "store_code";
    public const string StoreName = "store_name";
    /// <summary>Loja do perfil da intranet como está cadastrada (ex.: "04 - PRAZERES").</summary>
    public const string Loja = "loja";
    /// <summary>Roles originais do JWT da intranet (só informativo; as políticas usam "role").</summary>
    public const string IntranetRole = "intranet_role";

    /// <summary>Tipo de autenticação da identidade já transformada.</summary>
    public const string AuthenticationType = "Escala5x2";
}

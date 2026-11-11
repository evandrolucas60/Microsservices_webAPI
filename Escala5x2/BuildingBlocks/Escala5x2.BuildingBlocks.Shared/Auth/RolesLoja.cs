using BuildingBlocks.Shared.Security;
using Microsoft.Extensions.Configuration;

namespace Escala5x2.BuildingBlocks.Shared.Auth;

/// <summary>
/// Convenção de nome das roles por loja da intranet: "Escala5x2-Loja-{code}" (ex.: "Escala5x2-Loja-01", "Escala5x2-Loja-CD"),
/// configurável em <c>RolesLoja:Modelo</c> (o modelo precisa ter "{code}" uma vez; use o MESMO valor em todos os serviços
/// 5x2: o Store.Api cria as roles e os demais as leem do token). {code} é o código da loja no Store.Api como está
/// cadastrado; na leitura o código é comparado normalizado (<see cref="CodigoLoja.Normalizar"/>: "1" = "01"), sem
/// diferenciar maiúsculas e sem espaços nas pontas ("escala5x2-loja-01 " vale).
/// A role é criada no AuthAPI (mensagem GarantirRoles) quando a loja é criada/sincronizada e pelo "Garantir roles das
/// lojas" (Admin); o superAdmin a atribui no painel de gestão. Role de loja inativa NÃO é apagada (só deixa de aparecer
/// no seletor dos demais perfis, como a loja). Ver <see cref="EscopoLojas"/> para o papel que ela concede.
/// </summary>
public sealed class ModeloRoleLoja
{
    public const string Padrao = "Escala5x2-Loja-{code}";
    public const string ChaveConfiguracao = "RolesLoja:Modelo";
    private const string Marcador = "{code}";

    private readonly string _prefixo;
    private readonly string _sufixo;

    /// <summary>Modelo efetivo (o informado, ou o padrão quando vazio/sem "{code}").</summary>
    public string Modelo { get; }

    public ModeloRoleLoja(string? modelo = null)
    {
        modelo = modelo?.Trim();
        if (string.IsNullOrEmpty(modelo) || !Valido(modelo)) modelo = Padrao;
        Modelo = modelo;
        var i = modelo.IndexOf(Marcador, StringComparison.OrdinalIgnoreCase);
        _prefixo = modelo[..i];
        _sufixo = modelo[(i + Marcador.Length)..];
    }

    public static ModeloRoleLoja From(IConfiguration configuration) => new(configuration[ChaveConfiguracao]);

    /// <summary>Modelo com "{code}" exatamente uma vez e algum texto fixo (senão qualquer role viraria loja).</summary>
    private static bool Valido(string modelo)
    {
        var i = modelo.IndexOf(Marcador, StringComparison.OrdinalIgnoreCase);
        return i >= 0 && modelo.IndexOf(Marcador, i + 1, StringComparison.OrdinalIgnoreCase) < 0 && modelo.Length > Marcador.Length;
    }

    /// <summary>
    /// Nome da role da loja ("01" → "Escala5x2-Loja-01"); null quando o código está vazio ou o nome não seria aceito pelo
    /// painel de gestão da intranet (letras, números, ponto, hífen e sublinhado; até 50 caracteres).
    /// </summary>
    public string? NomeDaRole(string? codigoLoja)
    {
        var codigo = codigoLoja?.Trim();
        if (string.IsNullOrEmpty(codigo)) return null;
        var nome = _prefixo + codigo + _sufixo;
        return GarantidorDeRoles.Validar(nome) is null ? nome : null;
    }

    /// <summary>Código da loja de uma role da intranet ("Escala5x2-Loja-01" → "01"); null se a role não segue o modelo.</summary>
    public string? CodigoDaRole(string? role)
    {
        var r = role?.Trim();
        if (string.IsNullOrEmpty(r) || r.Length <= _prefixo.Length + _sufixo.Length) return null;
        if (!r.StartsWith(_prefixo, StringComparison.OrdinalIgnoreCase) || !r.EndsWith(_sufixo, StringComparison.OrdinalIgnoreCase)) return null;
        var codigo = r[_prefixo.Length..^_sufixo.Length].Trim();
        return codigo.Length > 0 ? codigo : null;
    }
}

/// <summary>
/// Papel efetivo por loja (regra única usada pelo <see cref="ICurrentUser"/> e espelhada no front):
/// <list type="bullet">
/// <item>Admin (superAdmin), RH e Jurídico: o próprio papel em TODAS as lojas (roles de loja não mudam nada).</item>
/// <item>Gestor da intranet: Gestor na loja do perfil e nas lojas das suas roles de loja.</item>
/// <item>Colaborador com role de loja ("operador"): Gestor (visualizar e gerar/editar/aprovar escalas, setores, turnos,
/// ausências...) SÓ nas lojas das roles; Colaborador (consulta) na loja do perfil. Nunca vira Admin/RH.</item>
/// <item>Colaborador sem role de loja: Colaborador só na loja do perfil.</item>
/// <item>Loja fora do conjunto: sem acesso (null).</item>
/// </list>
/// </summary>
public static class EscopoLojas
{
    public static bool Global(string? role) => role is Roles.Admin or Roles.RH or Roles.Juridico;

    /// <param name="role">Papel 5x2 da claim "role" (o Colaborador com role de loja já chega como Gestor).</param>
    /// <param name="lojas">Lojas que o usuário enxerga (perfil ∪ roles de loja).</param>
    /// <param name="gerenciadas">Lojas em que é Gestor.</param>
    public static string? PapelNaLoja(string? role, IReadOnlySet<Guid> lojas, IReadOnlySet<Guid> gerenciadas, Guid storeId)
    {
        if (Global(role)) return role;
        if (role == Roles.Gestor && gerenciadas.Contains(storeId)) return Roles.Gestor;
        return lojas.Contains(storeId) ? Roles.Colaborador : null;
    }
}

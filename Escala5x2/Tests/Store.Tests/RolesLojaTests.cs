using System.Security.Claims;
using BuildingBlocks.Contracts.Administracao;
using BuildingBlocks.Shared.Security;
using Escala5x2.BuildingBlocks.Shared.Auth;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Store.Tests;

/// <summary>Roles por loja ("Escala5x2-Loja-{code}"): convenção, lojas permitidas, papel efetivo por loja e criação idempotente.</summary>
public class RolesLojaTests
{
    private static readonly Guid Cabo = Guid.NewGuid();
    private static readonly Guid Prazeres = Guid.NewGuid();
    private static readonly Guid AsaBranca = Guid.NewGuid();
    private static readonly Guid Cd = Guid.NewGuid();

    private static readonly IReadOnlyList<LojaEscala> Lojas =
    [
        new(AsaBranca, "28", "ASA BRANCA"),
        new(Cabo, "01", "CABO"),
        new(Prazeres, "4", "PRAZERES"),
        new(Cd, "CD", "CENTRO DE DISTRIBUIÇÃO"),
    ];

    private static readonly ModeloRoleLoja Modelo = new();

    // ---------- Convenção de nome ----------

    [Fact]
    public void Nome_da_role_pelo_codigo_da_loja()
    {
        Assert.Equal("Escala5x2-Loja-{code}", Modelo.Modelo);
        Assert.Equal("Escala5x2-Loja-01", Modelo.NomeDaRole("01"));
        Assert.Equal("Escala5x2-Loja-CD", Modelo.NomeDaRole(" CD "));
        Assert.Null(Modelo.NomeDaRole(""));
        Assert.Null(Modelo.NomeDaRole("LOJA 1")); // espaço não é aceito pelo painel de gestão
    }

    [Theory]
    [InlineData("Escala5x2-Loja-01", "01")]
    [InlineData(" escala5x2-loja-01 ", "01")]
    [InlineData("ESCALA5X2-LOJA-cd", "cd")]
    [InlineData("Escala5x2-Loja- 7 ", "7")]
    [InlineData("Escala5x2-Loja-", null)]
    [InlineData("Gestor", null)]
    [InlineData("superAdmin", null)]
    [InlineData("Loja-01", null)]
    public void Codigo_da_role_sem_diferenciar_maiusculas_e_espacos(string role, string? codigo) =>
        Assert.Equal(codigo, Modelo.CodigoDaRole(role));

    [Fact]
    public void Modelo_configuravel_e_invalido_volta_ao_padrao()
    {
        var custom = new ModeloRoleLoja("Loja.{code}.5x2");
        Assert.Equal("Loja.01.5x2", custom.NomeDaRole("01"));
        Assert.Equal("01", custom.CodigoDaRole("loja.01.5X2"));
        Assert.Null(custom.CodigoDaRole("Escala5x2-Loja-01"));

        Assert.Equal(ModeloRoleLoja.Padrao, new ModeloRoleLoja("SemMarcador").Modelo);
        Assert.Equal(ModeloRoleLoja.Padrao, new ModeloRoleLoja("{code}").Modelo);
        Assert.Equal(ModeloRoleLoja.Padrao, new ModeloRoleLoja("{code}-{code}").Modelo);
        Assert.Equal(ModeloRoleLoja.Padrao, new ModeloRoleLoja(null).Modelo);
    }

    // ---------- Lojas permitidas (perfil ∪ roles) e papel efetivo ----------

    [Fact]
    public void Colaborador_sem_role_de_loja_fica_so_com_a_loja_do_perfil()
    {
        var a = IdentidadeEscalaService.CalcularAcesso(Roles.Colaborador, Cabo, ["Colaboradores"], Lojas, Modelo);

        Assert.Equal(Roles.Colaborador, a.Role);
        Assert.Equal(Cabo, a.StoreId);
        Assert.Equal([Cabo], a.StoreIds);
        Assert.Empty(a.ManagedStoreIds);
    }

    [Fact]
    public void Colaborador_com_role_de_loja_vira_Gestor_so_nas_lojas_das_roles()
    {
        // "Escala5x2-Loja-04" casa com o código "4"; "escala5x2-loja-28 " sem diferenciar maiúsculas/espaços
        var a = IdentidadeEscalaService.CalcularAcesso(Roles.Colaborador, Cabo,
            ["Escala5x2-Loja-04", " escala5x2-loja-28 ", "Escala5x2-Loja-99"], Lojas, Modelo);

        Assert.Equal(Roles.Gestor, a.Role);
        Assert.Equal(Cabo, a.StoreId); // principal continua a do perfil
        Assert.Equal([Cabo, Prazeres, AsaBranca], a.StoreIds); // perfil primeiro, depois as roles na ordem do código
        Assert.Equal([Prazeres, AsaBranca], a.ManagedStoreIds); // a do perfil continua só consulta
    }

    [Fact]
    public void Codigo_1_casa_com_a_loja_01()
    {
        var a = IdentidadeEscalaService.CalcularAcesso(Roles.Colaborador, null, ["Escala5x2-Loja-1"], Lojas, Modelo);

        Assert.Equal(Roles.Gestor, a.Role);
        Assert.Equal(Cabo, a.StoreId); // sem loja no perfil: a primeira das roles
        Assert.Equal([Cabo], a.StoreIds);
        Assert.Equal([Cabo], a.ManagedStoreIds);
    }

    [Fact]
    public void Gestor_gerencia_a_loja_do_perfil_e_as_das_roles()
    {
        var a = IdentidadeEscalaService.CalcularAcesso(Roles.Gestor, Cabo, ["Gestor", "Escala5x2-Loja-CD", "Escala5x2-Loja-01"], Lojas, Modelo);

        Assert.Equal(Roles.Gestor, a.Role);
        Assert.Equal([Cabo, Cd], a.StoreIds);
        Assert.Equal([Cabo, Cd], a.ManagedStoreIds);
    }

    [Theory]
    [InlineData(Roles.Admin)]
    [InlineData(Roles.RH)]
    [InlineData(Roles.Juridico)]
    public void Papeis_globais_nao_mudam_com_role_de_loja(string papel)
    {
        var a = IdentidadeEscalaService.CalcularAcesso(papel, null, ["Escala5x2-Loja-04"], Lojas, Modelo);

        Assert.Equal(papel, a.Role);
        Assert.Empty(a.ManagedStoreIds);
    }

    [Fact]
    public void Store_Api_fora_do_ar_nao_concede_nada_pelas_roles()
    {
        var a = IdentidadeEscalaService.CalcularAcesso(Roles.Colaborador, null, ["Escala5x2-Loja-01"], null, Modelo);

        Assert.Equal(Roles.Colaborador, a.Role);
        Assert.Null(a.StoreId);
        Assert.Empty(a.StoreIds);
    }

    [Fact]
    public void Role_de_loja_nao_vira_papel_da_intranet()
    {
        // Só superAdmin vira Admin; role de loja não é RH/Gestor/Admin
        Assert.Equal(Roles.Colaborador, IdentidadeEscalaService.MapearPapel(["Escala5x2-Loja-01"], ["superAdmin"]));
        Assert.Equal(Roles.Admin, IdentidadeEscalaService.MapearPapel(["Escala5x2-Loja-01", "superAdmin "], ["superAdmin"]));
    }

    // ---------- Usuário corrente com várias lojas ----------

    private static ICurrentUser Usuario(string role, Guid? store, IEnumerable<Guid> lojas, IEnumerable<Guid> gerenciadas)
    {
        var claims = new List<Claim> { new(EscalaClaims.Role, role) };
        if (store is { } s) claims.Add(new Claim(EscalaClaims.Store, s.ToString()));
        claims.AddRange(lojas.Select(id => new Claim(EscalaClaims.Stores, id.ToString())));
        claims.AddRange(gerenciadas.Select(id => new Claim(EscalaClaims.StoresGestor, id.ToString())));
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, EscalaClaims.AuthenticationType)) };
        return new CurrentUser(new HttpContextAccessor { HttpContext = http });
    }

    [Fact]
    public void Operador_ve_as_lojas_permitidas_e_gerencia_so_as_das_roles()
    {
        var user = Usuario(Roles.Gestor, Cabo, [Cabo, Prazeres, AsaBranca], [Prazeres, AsaBranca]);

        Assert.True(user.CanAccessStore(Cabo));
        Assert.True(user.CanAccessStore(Prazeres));
        Assert.True(user.CanAccessStore(AsaBranca));
        Assert.False(user.CanAccessStore(Cd));

        Assert.False(user.CanManageStore(Cabo)); // loja do perfil: Colaborador
        Assert.True(user.CanManageStore(Prazeres));
        Assert.False(user.CanManageStore(Cd));

        Assert.Equal(Roles.Colaborador, user.RoleIn(Cabo));
        Assert.Equal(Roles.Gestor, user.RoleIn(AsaBranca));
        Assert.Null(user.RoleIn(Cd));
        Assert.False(user.HasGlobalScope);
        Assert.Equal(3, user.StoreIds.Count);
    }

    [Fact]
    public void Loja_principal_sempre_permitida_e_RH_acessa_tudo_sem_gerenciar()
    {
        var gestor = Usuario(Roles.Gestor, Cabo, [], [Cabo]);
        Assert.True(gestor.CanAccessStore(Cabo));
        Assert.True(gestor.CanManageStore(Cabo));

        var rh = Usuario(Roles.RH, null, [], []);
        Assert.True(rh.CanAccessStore(Cd));
        Assert.False(rh.CanManageStore(Cd)); // gerar escala: Admin ou Gestor
        Assert.Equal(Roles.RH, rh.RoleIn(Cd));

        var admin = Usuario(Roles.Admin, null, [], []);
        Assert.True(admin.CanManageStore(Cd));

        var semLoja = Usuario(Roles.Colaborador, null, [], []);
        Assert.False(semLoja.CanAccessStore(Cabo));
    }

    // ---------- Criação idempotente das roles (consumidor do AuthAPI) ----------

    private sealed class ArmazemFalso : IArmazemRoles
    {
        public readonly HashSet<string> Roles = new(StringComparer.OrdinalIgnoreCase) { "superAdmin", "Gestor" };
        public int Criacoes;
        public string? CorridaCom;

        public Task<bool> ExisteAsync(string nome, CancellationToken ct) => Task.FromResult(Roles.Contains(nome) && nome != CorridaCom);

        public Task<(CriacaoRole Resultado, string? Erro)> CriarAsync(string nome, CancellationToken ct)
        {
            if (nome == CorridaCom) return Task.FromResult<(CriacaoRole, string?)>((CriacaoRole.JaExistia, null));
            Criacoes++;
            Roles.Add(nome);
            return Task.FromResult<(CriacaoRole, string?)>((CriacaoRole.Criada, null));
        }
    }

    [Fact]
    public async Task Garantir_roles_cria_so_as_que_faltam_e_e_idempotente()
    {
        var armazem = new ArmazemFalso();
        string[] nomes = ["Escala5x2-Loja-01", "escala5x2-loja-01", "Escala5x2-Loja-04 ", "Gestor", "Loja 7", ""];

        var primeira = await GarantidorDeRoles.GarantirAsync(nomes, armazem);
        Assert.Equal(["Escala5x2-Loja-01", "Escala5x2-Loja-04"], primeira.Criadas);
        Assert.Equal(["Gestor"], primeira.Existentes);
        Assert.Equal(2, primeira.Recusadas.Length); // "Loja 7" (espaço) e vazio

        var segunda = await GarantidorDeRoles.GarantirAsync(nomes, armazem);
        Assert.Empty(segunda.Criadas);
        Assert.Equal(["Escala5x2-Loja-01", "Escala5x2-Loja-04", "Gestor"], segunda.Existentes);
        Assert.Equal(2, armazem.Criacoes);
    }

    [Fact]
    public async Task Corrida_com_outro_pedido_conta_como_existente()
    {
        var armazem = new ArmazemFalso { CorridaCom = "Escala5x2-Loja-28" };

        RolesGarantidas r = await GarantidorDeRoles.GarantirAsync(["Escala5x2-Loja-28"], armazem);

        Assert.Empty(r.Criadas);
        Assert.Equal(["Escala5x2-Loja-28"], r.Existentes);
        Assert.Empty(r.Recusadas);
    }

    [Fact]
    public void Nome_longo_demais_e_recusado()
    {
        Assert.Null(Modelo.NomeDaRole(new string('9', 60)));
        Assert.NotNull(GarantidorDeRoles.Validar(new string('a', 51)));
        Assert.Null(GarantidorDeRoles.Validar("Escala5x2-Loja-01"));
    }
}

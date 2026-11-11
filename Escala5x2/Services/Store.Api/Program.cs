using Escala5x2.BuildingBlocks.Shared.Auth;
using Escala5x2.BuildingBlocks.Shared.Demo;
using Escala5x2.BuildingBlocks.Shared.Lg;
using Escala5x2.BuildingBlocks.Shared.Messaging;
using Escala5x2.BuildingBlocks.Shared.Persistence;
using Escala5x2.BuildingBlocks.Shared.Web;
using Microsoft.EntityFrameworkCore;
using Store.Api.Data;
using Store.Api.Domain;
using Store.Api.Endpoints;
using Store.Api.Lg;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.Services.AddPostgres<StoreDbContext>(builder.Configuration);
// Roles por loja na intranet ("Escala5x2-Loja-{code}"): o AuthAPI cria as que faltam (GarantirRoles)
builder.Services.AddEventBus(builder.Configuration, x => x.AddRequestClient<BuildingBlocks.Contracts.Administracao.GarantirRoles>());
builder.Services.AddScoped<RolesLojasIntranet>();
builder.Services.AddHostedService<RolesLojasNaInicializacao>();
// Loja do perfil da intranet → StoreId: aqui direto no banco (os demais serviços usam /internal/stores)
builder.Services.AddScoped<ILojasEscala, LojasEscalaDb>();
// Intervalo padrão (intervalo_padrao_minutos) para turnos criados sem BreakMinutes
builder.Services.AddHttpClient<ComplianceRulesClient>(c =>
    {
        c.BaseAddress = new Uri(builder.Configuration["Services:Compliance"] ?? "https://localhost:7023");
        c.Timeout = TimeSpan.FromSeconds(10);
    })
    .AddHttpMessageHandler<InternalKeyHandler>()
    .AcceptLocalDevCertificate(builder.Environment);

// Integração LG (SOAP): lojas a partir dos centros de custo (ConsultarListaCentroDeCustoCompletoPorDemanda). Mesma seção
// "LG" do Employee.Api; credenciais só por variável de ambiente/user-secrets/secrets (ver README). Sem configuração, a
// sincronização responde "Integração LG não configurada" e o resto do serviço funciona normalmente.
builder.Services.Configure<LgOptions>(builder.Configuration.GetSection(LgOptions.Section));
builder.Services.AddHttpClient<LgCentrosDeCustoClient>(c => c.Timeout = Timeout.InfiniteTimeSpan); // limite por chamada: LG:TimeoutSegundos
builder.Services.AddScoped<LgLojasSincronizador>();
// Setores a partir do OrgaoResponsavel da LG: catálogo da empresa (ConsultarListaPorDemanda, namespace api/v1) garantido
// em todas as lojas depois das lojas e no botão próprio; e o pedido interno do Employee.Api (órgãos dos colaboradores)
builder.Services.AddScoped<LgSetoresService>();
builder.Services.AddHttpClient<LgOrgaosResponsaveisClient>(c => c.Timeout = Timeout.InfiniteTimeSpan);
builder.Services.AddScoped<LgSetoresSincronizador>();
builder.Services.AddSingleton<LgSetoresRunner>();
builder.Services.AddSingleton<LgLojasRunner>();
builder.Services.AddHostedService<LgLojasSincronizacaoDiaria>();

var app = builder.Build();
app.UseServiceDefaults();
app.MapStoreEndpoints();
// Cadastro da loja (localização e horário de funcionamento), picos manuais e funcionamento da loja nos feriados
app.MapStoreCadastroEndpoints();
app.MapLgLojasEndpoints();
app.MapHolidayEndpoints();
// Identidade 5x2 do usuário logado (papel e loja); o gateway expõe como /api/escala5x2/me
app.MapMeEndpoint();
// Arquivos de log do serviço (só Admin): /api/stores/logs
app.MapLogFileEndpoints("/api/stores");

// Atualiza o banco como os serviços da intranet (ApplyMigrations): aplica as migrations pendentes
await app.Services.ApplyMigrationsAsync<StoreDbContext>();
if (app.Configuration.GetValue<bool>("Seed:Demo"))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<StoreDbContext>();
    if (!await db.Stores.AnyAsync(s => s.Id == DemoData.StoreId))
    {
        var store = new Store.Api.Domain.Store
        {
            Id = DemoData.StoreId, Code = "DEMO", Name = "Loja Demonstração", City = "Cabo de Santo Agostinho",
            State = "PE", Format = StoreFormat.Varejo
        };
        store.Sectors.Add(new Sector { Id = DemoData.SectorCaixa, StoreId = store.Id, Name = "Frente de caixa" });
        store.Shifts.Add(new Shift { Id = DemoData.ShiftManha, StoreId = store.Id, Code = "M", Start = new TimeOnly(7, 0), End = new TimeOnly(15, 20), BreakMinutes = 60 });
        store.Shifts.Add(new Shift { Id = DemoData.ShiftTarde, StoreId = store.Id, Code = "T", Start = new TimeOnly(13, 40), End = new TimeOnly(22, 0), BreakMinutes = 60 });

        // Mesma demanda do exemplo do Documento de Alinhamento: 5/dia útil, 6 no sábado, 4 no domingo.
        foreach (var day in Enum.GetValues<DayOfWeek>())
        {
            var (morning, evening) = day switch
            {
                DayOfWeek.Saturday => (4, 2),
                DayOfWeek.Sunday => (2, 2),
                _ => (3, 2)
            };
            store.Demands.Add(new StaffDemand { StoreId = store.Id, SectorId = DemoData.SectorCaixa, DayOfWeek = day, Start = new TimeOnly(8, 0), End = new TimeOnly(13, 30), MinStaff = morning });
            store.Demands.Add(new StaffDemand { StoreId = store.Id, SectorId = DemoData.SectorCaixa, DayOfWeek = day, Start = new TimeOnly(15, 30), End = new TimeOnly(21, 30), MinStaff = evening });
        }
        // Feriados nacionais fixos (Natal etc.) vêm da migration HolidayScopes, como recorrentes
        db.Stores.Add(store);
        // Operação 07h–22h, mínimos 2/2/2, entradas flexíveis de 15 em 15 min, jornada de 8h20 com 1h de intervalo
        // (bancos existentes recebem a mesma operação pela migration OperacaoLoja)
        db.Operations.Add(new StoreOperation
        {
            StoreId = store.Id, Abertura = new TimeOnly(7, 0), Fechamento = new TimeOnly(22, 0), CoberturaMinima = 2, MinimoAbertura = 2,
            MinimoFechamento = 2, IncrementoFlexivelMinutos = 15, DuracaoJornadaMinutos = 500, IntervaloMinutos = 60, AtualizadoPor = "seed"
        });
        await db.SaveChangesAsync();
    }
}
// Lojas da rede (lista oficial da intranet) que faltam no cadastro. Com a LG configurada para centros de custo, as lojas
// vêm da sincronização de lojas (nome e código LG do centro de custo) e o seed fica DESLIGADO por padrão: ele criaria
// lojas só com o nome curto da intranet (e lojas que a LG não tem). Sem LG, continua como fallback. Seed:LojasRede
// true/false explícito prevalece. Lojas já semeadas são adotadas pela sincronização pelo código ("01" = "1").
var lgLojas = app.Configuration.GetSection(LgOptions.Section).Get<LgOptions>() ?? new LgOptions();
if (app.Configuration.GetValue<bool?>("Seed:LojasRede") ?? !lgLojas.ConfiguradaPara(LgOperacao.CentrosDeCusto))
    await app.Services.CadastrarLojasDaRedeAsync();

app.Run();

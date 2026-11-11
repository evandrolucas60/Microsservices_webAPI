using Escala5x2.BuildingBlocks.Contracts.Planning;
using Escala5x2.BuildingBlocks.Shared.Demo;
using Escala5x2.BuildingBlocks.Shared.Lg;
using Escala5x2.BuildingBlocks.Shared.Messaging;
using Escala5x2.BuildingBlocks.Shared.Persistence;
using Escala5x2.BuildingBlocks.Shared.Web;
using Employee.Api.Data;
using Employee.Api.Domain;
using Employee.Api.Endpoints;
using Employee.Api.Lg;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.Services.AddPostgres<EmployeeDbContext>(builder.Configuration);
builder.Services.AddEventBus(builder.Configuration);

// Integração LG (SOAP): colaboradores, situação (férias/afastamento) e loja pelo centro de custo. Credenciais só por
// variável de ambiente/user-secrets/secrets (seção "LG"; ver README). Sem configuração, a sincronização responde
// "Integração LG não configurada" e o resto do serviço funciona normalmente.
builder.Services.Configure<LgOptions>(builder.Configuration.GetSection(LgOptions.Section));
builder.Services.AddHttpClient<LgSoapClient>(c => c.Timeout = Timeout.InfiniteTimeSpan); // limite por chamada: LG:TimeoutSegundos
builder.Services.AddTransient<InternalKeyHandler>();
// Setores pelo OrgaoResponsavel: o Store.Api garante um setor por órgão em cada loja (POST /internal/stores/{id}/sectors/garantir-lg)
builder.Services.AddHttpClient(SetoresLgLojaHttp.Cliente, c =>
    {
        c.BaseAddress = new Uri(builder.Configuration["Services:Store"] ?? "http://localhost:7022");
        c.Timeout = TimeSpan.FromSeconds(30);
    })
    .AddHttpMessageHandler<InternalKeyHandler>()
    .AcceptLocalDevCertificate(builder.Environment);
builder.Services.AddScoped<ISetoresLgLoja, SetoresLgLojaHttp>();
// Sincronização de lojas no Store.Api antes da de colaboradores (opcional: LG:Lojas:SincronizarAntesDosColaboradores)
builder.Services.AddHttpClient(SincronizacaoLojasLgHttp.Cliente, c =>
    {
        c.BaseAddress = new Uri(builder.Configuration["Services:Store"] ?? "http://localhost:7022");
        c.Timeout = TimeSpan.FromMinutes(10); // centros de custo na LG e, em seguida, o catálogo de setores (órgãos) em todas as lojas
    })
    .AddHttpMessageHandler<InternalKeyHandler>()
    .AcceptLocalDevCertificate(builder.Environment);
builder.Services.AddScoped<ISincronizacaoLojasLg, SincronizacaoLojasLgHttp>();
// Férias (ServicoDeFerias/ConsultarLista, uma chamada por matrícula): depois da sincronização de colaboradores, no botão
// "Atualizar férias" e antes de cada geração de escala (POST /internal/planning/ferias-lg). LG:Operacoes:Ferias:Url/SoapAction.
builder.Services.AddHttpClient<LgFeriasClient>(c => c.Timeout = Timeout.InfiniteTimeSpan); // limite por chamada: LG:TimeoutSegundos
builder.Services.AddScoped<LgFeriasSincronizador>();
builder.Services.AddSingleton<LgFeriasAtualizador>();
builder.Services.AddScoped<LgSincronizador>();
builder.Services.AddSingleton<LgSincronizacaoRunner>();
builder.Services.AddHostedService<LgSincronizacaoDiaria>();

var app = builder.Build();
app.UseServiceDefaults();
app.MapEmployeeEndpoints();
// Arquivos de log do serviço (só Admin): /api/employees/logs
app.MapLogFileEndpoints("/api/employees");

// Atualiza o banco como os serviços da intranet (ApplyMigrations): aplica as migrations pendentes
await app.Services.ApplyMigrationsAsync<EmployeeDbContext>();
if (app.Configuration.GetValue<bool>("Seed:Demo"))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<EmployeeDbContext>();
    if (!await db.Employees.AnyAsync(e => e.StoreId == DemoData.StoreId))
    {
        var n = 0;
        foreach (var (registration, name, biweekly) in DemoData.Employees)
        {
            db.Employees.Add(new Employee.Api.Domain.Employee
            {
                // Mesmo padrão da migration PerfilJornada: abertura e fechamento alternados
                PerfilJornada = n++ % 2 == 0 ? JourneyProfiles.Abertura : JourneyProfiles.Fechamento,
                Registration = registration,
                Name = name,
                StoreId = DemoData.StoreId,
                JobTitle = "Operador(a) de caixa",
                Sectors = [new EmployeeSector { SectorId = DemoData.SectorCaixa }],
                Contracts =
                [
                    new ContractVersion
                    {
                        ValidFrom = new DateOnly(2026, 1, 1),
                        JourneyControlled = true,
                        SundayRule = biweekly ? SundayRule.Biweekly : SundayRule.OneInThreeWeeks,
                        LegalOpinionRef = biweekly ? "DEMO - parecer pendente" : null,
                        CreatedBy = "seed"
                    }
                ]
            });
        }
        await db.SaveChangesAsync();
    }
}

app.Run();

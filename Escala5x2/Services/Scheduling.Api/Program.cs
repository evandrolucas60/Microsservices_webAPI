using Escala5x2.BuildingBlocks.Shared.Messaging;
using Escala5x2.BuildingBlocks.Shared.Persistence;
using Escala5x2.BuildingBlocks.Shared.Web;
using MassTransit;
using Scheduling.Api.Clients;
using Scheduling.Api.Consumers;
using Scheduling.Api.Data;
using Scheduling.Api.Endpoints;
using Scheduling.Api.Services;
using Escala5x2.BuildingBlocks.Compliance;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

// Sem retry automático do Npgsql: o outbox do MassTransit controla a transação.
// Consultas divididas: Include(Assignments) numa consulta só repetia a fotografia e a conformidade (jsonb de centenas
// de KB) em cada célula — a escala de 6 semanas levava ~10 s para abrir (dezenas de MB trafegados).
builder.Services.AddPostgres<SchedulingDbContext>(builder.Configuration, retryOnFailure: false, splitQueries: true);

builder.Services.AddTransient<InternalKeyHandler>();
foreach (var (name, key) in new[] { (PlanningClients.Employee, "Employee"), (PlanningClients.Store, "Store"), (PlanningClients.Compliance, "Compliance") })
{
    builder.Services.AddHttpClient(name, c =>
        {
            c.BaseAddress = new Uri(builder.Configuration[$"Services:{key}"] ?? throw new InvalidOperationException($"Services:{key} não configurado."));
            c.Timeout = TimeSpan.FromSeconds(30);
        })
        .AddHttpMessageHandler<InternalKeyHandler>()
        // Em Development os serviços usam https://localhost:702x com o certificado de desenvolvimento
        .AcceptLocalDevCertificate(builder.Environment);
}
builder.Services.AddScoped<PlanningClients>();
builder.Services.AddScoped<ScheduleRequester>();
builder.Services.AddScoped<SnapshotBuilder>();
builder.Services.AddScoped<ScheduleEvaluation>();
builder.Services.AddScoped<AbsenceSync>();
// Mesmo avaliador de conformidade do Optimization.Worker
builder.Services.AddSingleton<ComplianceEvaluator>();

builder.Services.AddEventBus(builder.Configuration, x =>
{
    x.AddEntityFrameworkOutbox<SchedulingDbContext>(o =>
    {
        o.UsePostgres();
        o.UseBusOutbox();
    });
    x.AddConfigureEndpointsCallback((context, _, cfg) => cfg.UseEntityFrameworkOutbox<SchedulingDbContext>(context));
    x.AddConsumer<ScheduleResultConsumers>();
    // Ausência registrada/alterada/excluída no Employee.Api: reavalia as escalas editáveis do período
    x.AddConsumer<EmployeeAbsenceConsumer>();
});

var app = builder.Build();
app.UseServiceDefaults();
app.MapScheduleEndpoints();
// Visão do dia: ajuste dos intervalos de refeição
app.MapDayEndpoints();
// Logs para o superAdmin: opções do motor (CSV/JSON) e arquivos do Serilog deste serviço e do Worker
app.MapLogEndpoints();
// Consulta somente leitura das escalas publicadas (página "Escalas" da intranet)
app.MapConsultaEndpoints();

// Atualiza o banco como os serviços da intranet (ApplyMigrations): aplica as migrations pendentes
await app.Services.ApplyMigrationsAsync<SchedulingDbContext>();
app.Run();

using Dimensionamento.Api.Clients;
using Dimensionamento.Api.Data;
using Dimensionamento.Api.Endpoints;
using Dimensionamento.Api.Services;
using Escala5x2.BuildingBlocks.Shared.Messaging;
using Escala5x2.BuildingBlocks.Shared.Persistence;
using Escala5x2.BuildingBlocks.Shared.Web;

// Dimensionamento do quadro (Escala 5x2): demanda por hora, necessidade (Erlang/utilização), quadro mínimo por carga
// total × por cobertura temporal (CP-SAT), disponíveis, déficit, contratações e sensibilidade. Gateway: /api/escala5x2/dimensionamento.
var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.Services.AddPostgres<DimensionamentoDbContext>(builder.Configuration);
// Só para a autenticação da intranet (loja do perfil no IdentidadeAPI e bloqueio no AuthAPI): o serviço não publica eventos
builder.Services.AddEventBus(builder.Configuration);

var timeout = TimeSpan.FromSeconds(builder.Configuration.GetValue("Dimensionamento:TimeoutServicosSegundos", 5));
foreach (var (name, key) in new[]
         {
             (EscalaClients.Employee, "Employee"), (EscalaClients.Store, "Store"),
             (EscalaClients.Compliance, "Compliance"), (EscalaClients.Scheduling, "Scheduling"),
         })
{
    builder.Services.AddHttpClient(name, c =>
        {
            c.BaseAddress = new Uri(builder.Configuration[$"Services:{key}"] ?? throw new InvalidOperationException($"Services:{key} não configurado."));
            // Curto: sem o serviço o dimensionamento degrada com aviso, não espera
            c.Timeout = timeout;
        })
        .AddHttpMessageHandler<InternalKeyHandler>()
        // Em Development os serviços usam https://localhost:702x com o certificado de desenvolvimento
        .AcceptLocalDevCertificate(builder.Environment);
}
builder.Services.AddScoped<EscalaClients>();
builder.Services.AddScoped<SimulacaoService>();

var app = builder.Build();
app.UseServiceDefaults();
app.MapDimensionamentoEndpoints();
// Arquivos de log do serviço (só Admin): /api/dimensionamento/logs
app.MapLogFileEndpoints("/api/dimensionamento");

// Atualiza o banco como os serviços da intranet (ApplyMigrations): aplica as migrations pendentes
await app.Services.ApplyMigrationsAsync<DimensionamentoDbContext>();
app.Run();

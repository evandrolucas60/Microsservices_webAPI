using Escala5x2.BuildingBlocks.Shared.Messaging;
using Escala5x2.BuildingBlocks.Shared.Persistence;
using Escala5x2.BuildingBlocks.Shared.Web;
using Compliance.Api.Data;
using Compliance.Api.Endpoints;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.Services.AddPostgres<ComplianceDbContext>(builder.Configuration);
builder.Services.AddEventBus(builder.Configuration);

var app = builder.Build();
app.UseServiceDefaults();
app.MapRuleEndpoints();
// Arquivos de log do serviço (só Admin): /api/rules/logs
app.MapLogFileEndpoints("/api/rules");

// Atualiza o banco como os serviços da intranet (ApplyMigrations): aplica as migrations pendentes.
// A migration ComplianceRules converte as antigas versões legais e semeia o cadastro de regras de conformidade.
await app.Services.ApplyMigrationsAsync<ComplianceDbContext>();

app.Run();

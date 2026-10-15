using BuildingBlocks.Shared.Logging;
using BuildingBlocks.Shared.Messaging;
using BuildingBlocks.Shared.Middleware;
using BuildingBlocks.Shared.Security;
using Intranet.Services.AuditoriaAPI.Consumers;
using Intranet.Services.AuditoriaAPI.Data;
using Intranet.Services.AuditoriaAPI.Extensions;
using Intranet.Services.AuditoriaAPI.Services;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

var builder = WebApplication.CreateBuilder(args);

// Logs estruturados
builder.AddSharedSerilog();

// Banco da auditoria (erros e acessos)
builder.Services.AddDbContext<AppDbContext>(options =>
{
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection"));
    // Migrations escritas à mão: diferenças cosméticas no snapshot não devem impedir a inicialização
    options.ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
});

// Mensageria (MassTransit + RabbitMQ): recebe ErroRegistrado (todos os serviços) e AcessoRegistrado (AuthAPI)
builder.AddMessaging(x => x.AddConsumers(typeof(ErroRegistradoConsumer).Assembly));

// Retenção: apaga registros mais antigos que Auditoria:RetencaoDias (padrão 90)
builder.Services.AddHostedService<RetencaoWorker>();

builder.Services.AddControllers();
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// O frontend (painel do superAdmin) lê os logs paginados
builder.Services.AddCors(options =>
{
    options.AddPolicy("DefaultPolicy", policy =>
    {
        policy.WithOrigins("http://localhost:5173", "http://10.11.10.5:31")
              .AllowAnyMethod()
              .AllowAnyHeader()
              .AllowCredentials()
              .WithExposedHeaders("X-Pagination", "Authorization");
    });
});

builder.AddAppAuthentication();
// Política do painel de gestão (papel superAdmin)
builder.AddAdminPolicies();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseSharedRequestLogging();
app.UseHttpsRedirection();
app.UseCors("DefaultPolicy");
app.UseSharedExceptionHandling();

app.UseAuthentication();
// Barra usuários bloqueados pelo superAdmin (403)
app.UseSharedUserAccess();
app.UseAuthorization();

app.MapControllers();

ApplyMigrations();
app.Run();

void ApplyMigrations()
{
    using var scope = app.Services.CreateScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    if (dbContext.Database.GetPendingMigrations().Any())
    {
        dbContext.Database.Migrate();
    }
}

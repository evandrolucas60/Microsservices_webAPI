using BuildingBlocks.Shared.Logging;
using BuildingBlocks.Shared.Messaging;
using BuildingBlocks.Shared.Middleware;
using Intranet.Services.NotificacaoAPI.Consumers;
using MassTransit;
using Intranet.Services.NotificacaoAPI.Data;
using Intranet.Services.NotificacaoAPI.Extensions;
using Intranet.Services.NotificacaoAPI.Models;
using Intranet.Services.NotificacaoAPI.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

var builder = WebApplication.CreateBuilder(args);

// Logs estruturados
builder.AddSharedSerilog();

// Banco das notificações (fila persistente de envio)
builder.Services.AddDbContext<AppDbContext>(options =>
{
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection"));
    // Migrations escritas à mão: diferenças cosméticas no snapshot não devem impedir a inicialização
    options.ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
});

// Configuração de e-mail
builder.Services.Configure<SmtpOptions>(builder.Configuration.GetSection("Smtp"));
builder.Services.Configure<NewsletterOptions>(builder.Configuration.GetSection("Newsletter"));

// Newsletter de artigos: fila em memória + worker
builder.Services.AddSingleton<NewsletterQueue>();
builder.Services.AddHostedService<NewsletterWorker>();

// Notificações: canais de envio (send) + worker da fila persistente
builder.Services.AddScoped<INotificationSender, PushNotificationSender>();
builder.Services.AddScoped<INotificationSender, EmailNotificationSender>();
builder.Services.AddHostedService<NotificationDispatchWorker>();

// Mensageria (MassTransit + RabbitMQ): consome os pedidos de notificação e de newsletter dos
// outros serviços; consulta colaboradores/e-mails no IdentidadeAPI por requisição/resposta
builder.AddMessaging(x => x.AddConsumers(typeof(NotificacaoSolicitadaConsumer).Assembly));
builder.Services.AddScoped<MemberResolver>();

builder.Services.AddControllers();
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// O frontend lê as notificações do usuário (sininho)
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
builder.Services.AddAuthorization();

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

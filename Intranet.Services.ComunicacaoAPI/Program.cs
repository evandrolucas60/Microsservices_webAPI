using BuildingBlocks.Shared.Logging;
using BuildingBlocks.Shared.Messaging;
using BuildingBlocks.Shared.Middleware;
using BuildingBlocks.Shared.Security;
using Intranet.Services.ComunicacaoAPI.Data;
using Intranet.Services.ComunicacaoAPI.Extensions;
using Intranet.Services.ComunicacaoAPI.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

var builder = WebApplication.CreateBuilder(args);

// Logs estruturados
builder.AddSharedSerilog();

builder.Services.AddDbContext<AppDbContext>(options =>
{
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection"));
    // Migrations escritas à mão: diferenças cosméticas no snapshot não devem impedir a inicialização
    options.ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
});

// IdentidadeAPI (membro do token, validação de destinatários) e NotificacaoAPI
builder.AddMessaging();
builder.Services.AddScoped<IdentidadeClient>();
builder.Services.AddScoped<NotificationClient>();

// Chat em tempo real: conexões WebSocket e presença (online/offline)
builder.Services.AddSingleton<ChatConnectionManager>();

var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? new[] { "http://localhost:5173", "http://10.11.10.5:31" };

builder.Services.AddControllers();
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddCors(options =>
{
    options.AddPolicy("DefaultPolicy", policy =>
    {
        policy.WithOrigins(allowedOrigins)
              .AllowAnyMethod()
              .AllowAnyHeader()
              .AllowCredentials()
              .WithExposedHeaders("X-Pagination", "Authorization", "Content-Disposition");
    });
});

builder.AddAppAuthentication();
builder.Services.AddAuthorization();
// Painel de gestão: política [Authorize(Policy = AdminPolicies.SuperAdmin)]
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


app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

app.UseAuthentication();
// Usuários bloqueados pelo superAdmin recebem 403 (inclusive na conexão do /ws/chat,
// que passa por aqui depois da autenticação pelo access_token)
app.UseSharedUserAccess();
app.UseAuthorization();

app.MapControllers();
app.MapChatSocket(allowedOrigins);

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

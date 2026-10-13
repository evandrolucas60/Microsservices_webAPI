using Intranet.Services.ComunicacaoAPI.Data;
using Intranet.Services.ComunicacaoAPI.Extensions;
using Intranet.Services.ComunicacaoAPI.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>(options =>
{
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection"));
    // Migrations escritas à mão: diferenças cosméticas no snapshot não devem impedir a inicialização
    options.ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
});

// IdentidadeAPI (membro do token, validação de destinatários) e NotificacaoAPI
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient(IdentidadeClient.HttpClientName, client =>
{
    client.BaseAddress = new Uri(builder.Configuration["ServicesUrls:IdentidadeAPI"] ?? "https://localhost:7004/");
    client.Timeout = TimeSpan.FromSeconds(10);
}).AcceptLocalDevCertificate(builder.Environment);
builder.Services.AddHttpClient(NotificationClient.HttpClientName, client =>
{
    client.BaseAddress = new Uri(builder.Configuration["ServicesUrls:NotificacaoAPI"] ?? "https://localhost:7009/");
    client.Timeout = TimeSpan.FromSeconds(10);
}).AcceptLocalDevCertificate(builder.Environment);
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

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseCors("DefaultPolicy");

// Erro inesperado vira JSON depois do CORS: o navegador recebe a mensagem em vez de
// um genérico "Failed to fetch" (a página de exceção não leva os cabeçalhos CORS)
app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (Exception ex) when (!context.Response.HasStarted && !context.RequestAborted.IsCancellationRequested)
    {
        app.Logger.LogError(ex, "Erro não tratado em {Path}", context.Request.Path);
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await context.Response.WriteAsJsonAsync(new Intranet.Services.ComunicacaoAPI.Models.DTOs.ResponseDto
        {
            IsSuccess = false,
            Message = "Erro interno no serviço de comunicação. Tente novamente.",
        });
    }
});

app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

app.UseAuthentication();
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

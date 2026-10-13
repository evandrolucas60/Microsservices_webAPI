using Intranet.Services.NotificacaoAPI.Data;
using Intranet.Services.NotificacaoAPI.Extensions;
using Intranet.Services.NotificacaoAPI.Models;
using Intranet.Services.NotificacaoAPI.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

var builder = WebApplication.CreateBuilder(args);

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
builder.Services.AddSingleton<ServiceKeyValidator>();

// IdentidadeAPI: destinatários e membro do token
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient(RecipientsClient.HttpClientName, client =>
{
    client.BaseAddress = new Uri(builder.Configuration["ServicesUrls:IdentidadeAPI"] ?? "https://localhost:7004/");
    client.Timeout = TimeSpan.FromSeconds(15);
}).AcceptLocalDevCertificate(builder.Environment);
builder.Services.AddScoped<RecipientsClient>();
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

app.UseHttpsRedirection();
app.UseCors("DefaultPolicy");

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

using Intranet.Services.ComunidadesAPI.Data;
using Intranet.Services.ComunidadesAPI.Extensions;
using Intranet.Services.ComunidadesAPI.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>(options =>
{
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection"));
    // Migrations escritas à mão: diferenças cosméticas no snapshot não devem impedir a inicialização
    options.ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
});

// IdentidadeAPI (membro do token, validação de membros) e NotificacaoAPI
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

builder.Services.AddControllers();
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

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

using BuildingBlocks.Shared.Logging;
using BuildingBlocks.Shared.Messaging;
using BuildingBlocks.Shared.Middleware;
using BuildingBlocks.Shared.Security;
using Intranet.Services.GerenciamentoCategoriaAPI.Data;
using Intranet.Services.GerenciamentoCategoriaAPI.Extensions;
using Intranet.Services.GerenciamentoCategoriaAPI.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.OpenApi;

var builder = WebApplication.CreateBuilder(args);

// Logs estruturados e mensageria (MassTransit + RabbitMQ): necessária para consultar
// bloqueios/restrições dos usuários definidos no painel de gestão (AuthAPI)
builder.AddSharedSerilog();
builder.AddMessaging();

// Banco do GC (categorias e seus materiais)
builder.Services.AddDbContext<AppDbContext>(options =>
{
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection"));
    // Migrations escritas à mão: diferenças cosméticas no snapshot não devem impedir a inicialização
    options.ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
});

// Categorias do GC: papéis que podem gerenciar (Categorias:RolesGerenciamento)
builder.Services.AddSingleton<CategoriaPermissoes>();

var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? new[] { "http://localhost:5173", "http://10.11.10.5:31" };

builder.Services.AddCors(options =>
{
    options.AddPolicy("DefaultPolicy", policy =>
    {
        policy.WithOrigins(allowedOrigins)
              .AllowAnyMethod()
              .AllowAnyHeader()
              .AllowCredentials()
              .WithExposedHeaders("X-Pagination", "Content-Disposition");
    });
});

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

builder.Services.AddSwaggerGen(options =>
{
    const string schemeName = JwtBearerDefaults.AuthenticationScheme;

    options.AddSecurityDefinition(schemeName, new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Description = "Insira seu token JWT.",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT"
    });

    options.AddSecurityRequirement(document =>
        new OpenApiSecurityRequirement
        {
            {
                new OpenApiSecuritySchemeReference(schemeName, document),
                new List<string>()
            }
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

app.UseAuthentication();
// Usuários bloqueados pelo superAdmin recebem 403
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

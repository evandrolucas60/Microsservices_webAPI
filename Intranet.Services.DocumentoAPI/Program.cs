using BuildingBlocks.Shared.Logging;
using BuildingBlocks.Shared.Middleware;
using Intranet.Services.DocumentoAPI.Data;
using Intranet.Services.DocumentoAPI.Extensions;
using Intranet.Services.DocumentoAPI.Models.DTOs;
using Intranet.Services.DocumentoAPI.Repository.IRepository;
using Intranet.Services.DocumentoAPI.Services;
using Intranet.Services.EscalaAPI.Repository;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.OpenApi;

public partial class Program
{
    private static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Logs estruturados
        builder.AddSharedSerilog();

        // Add services to the container.
        var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");

        builder.Services.AddDbContext<AppDbContext>(options =>
        {
            options.UseNpgsql(connectionString);
            // Migrations escritas à mão: diferenças cosméticas no snapshot não devem impedir a inicialização
            options.ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
        });

        builder.Services.AddScoped(typeof(IRepository<>), typeof(Repository<>));
        builder.Services.AddScoped<IUnityOfWork, UnityOfWork>();

        // Lojas do GC: papéis que podem gerenciar (Lojas:RolesGerenciamento)
        builder.Services.AddSingleton<LojaPermissoes>();

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
                new OpenApiSecuritySchemeReference(
                    schemeName,
                    document
                ),
                new List<string>()
            }
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

            if (dbContext.Database.GetPendingMigrations().Count() > 0)
            {
                dbContext.Database.Migrate();
            }
        }
    }
}
using BuildingBlocks.Shared.Logging;
using BuildingBlocks.Shared.Messaging;
using BuildingBlocks.Shared.Middleware;
using Intranet.Services.EscalaAPI.Services;
using Intranet.Services.EscalaAPI.Data;
using Intranet.Services.EscalaAPI.Extensions;
using Intranet.Services.EscalaAPI.Repository;
using Intranet.Services.EscalaAPI.Repository.IRepository;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
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
            // Migrações escritas à mão (sem Designer): o snapshot é mantido manualmente
            options.ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning));
        });

        builder.Services.AddScoped(typeof(IRepository<>), typeof(Repository<>));
        builder.Services.AddScoped<IUnityOfWork, UnityOfWork>();

        // Mensageria (MassTransit + RabbitMQ): colaboradores da T.I consultados no IdentidadeAPI
        builder.AddMessaging();
        builder.Services.AddScoped<IdentidadeClient>();

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

        // Portal (Vite em desenvolvimento e servidor interno): a consulta das escalas é pública
        builder.Services.AddCors(options =>
        {
            options.AddPolicy("DefaultPolicy", policy =>
            {
                policy.WithOrigins(builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
                        ?? new[] { "http://localhost:5173", "http://10.11.10.5:31" })
                      .AllowAnyMethod()
                      .AllowAnyHeader()
                      .AllowCredentials()
                      .WithExposedHeaders("X-Pagination");
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
using MassTransit;
using BuildingBlocks.Shared.Http;
using BuildingBlocks.Shared.Logging;
using BuildingBlocks.Shared.Messaging;
using BuildingBlocks.Shared.Middleware;
using BuildingBlocks.Shared.Security;
using Intranet.Services.IdentidadeAPI.Consumers;
using AutoMapper;
using Intranet.Services.IdentidadeAPI;
using Intranet.Services.IdentidadeAPI.Data;
using Intranet.Services.IdentidadeAPI.Extensions;
using Intranet.Services.IdentidadeAPI.Repository;
using Intranet.Services.IdentidadeAPI.Repository.IRepository;
using Intranet.Services.IdentidadeAPI.Service;
using Intranet.Services.IdentidadeAPI.Service.IService;
using Intranet.Services.IdentidadeAPI.Utility;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.OpenApi;

public partial class Program
{
    private static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Logs estruturados (Serilog) e mensageria entre serviços (MassTransit + RabbitMQ):
        // o IdentidadeAPI responde às consultas de colaboradores dos demais serviços
        builder.AddSharedSerilog();
        builder.AddMessaging(x => x.AddConsumers(typeof(ObterMembroPorUsuarioConsumer).Assembly));

        // Add services to the container.
        var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");

        builder.Services.AddDbContext<AppDbContext>(options =>
        {
            options.UseNpgsql(connectionString);
            // Migrations escritas à mão (sem Designer): o snapshot é mantido manualmente e
            // pequenas diferenças não devem impedir o Migrate() na inicialização
            options.ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
        });

        builder.Services.AddScoped(typeof(IRepository<>), typeof(Repository<>));
        builder.Services.AddScoped<IUnityOfWork, UnityOfWork>();
        builder.Services.AddScoped<IWorkRepository, WorkRepository>();
        builder.Services.AddScoped<IEducationRepository, EducationRepository>();
        builder.Services.AddScoped<IWorkService, WorkService>();
        builder.Services.AddScoped<IEducationService, EducationService>();
        // Gestor direto / organograma (também usado pelos consumers de hierarquia)
        builder.Services.AddScoped<IHierarquiaService, HierarquiaService>();
        builder.Services.AddScoped<BackendApiAuthenticationHttpClientHandler>();
        builder.Services.AddScoped<IOwnershipChecker, OwnershipChecker>();
        builder.Services.AddHttpContextAccessor();

        builder.Services.AddHttpClient("Work", u => u.BaseAddress =
            new Uri(builder.Configuration
            ["ServicesUrls:BaseAddress"])).AddHttpMessageHandler<BackendApiAuthenticationHttpClientHandler>()
            .AcceptLocalDevCertificate(builder.Environment);

        builder.Services.AddHttpClient("Education", u => u.BaseAddress =
            new Uri(builder.Configuration
            ["ServicesUrls:BaseAddress"])).AddHttpMessageHandler<BackendApiAuthenticationHttpClientHandler>()
            .AcceptLocalDevCertificate(builder.Environment);

        // AutoMapper 13+ já inclui a integração com DI (sem AutoMapper.Extensions.Microsoft.DependencyInjection)
        builder.Services.AddAutoMapper(cfg => { }, typeof(MappingConfig));

        // Erros de validação automática (400) com mensagens em pt-BR
        builder.Services.AddControllers()
            .ConfigureApiBehaviorOptions(options =>
                options.InvalidModelStateResponseFactory = ValidationMessages.InvalidModelStateResponse);
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

        builder.Services.AddCors(options =>
        {
            options.AddPolicy("DefaultPolicy", builder =>
            {
                builder.WithOrigins("http://localhost:5173", "http://10.11.10.5:31")
                       .AllowAnyMethod()
                       .AllowAnyHeader()
                       .AllowCredentials()
                       .WithExposedHeaders("Authorization", "X-Pagination");
            });
        });

        builder.AddAppAuthentication();

        builder.Services.AddAuthorization();
        // Painel de gestão: política [Authorize(Policy = AdminPolicies.SuperAdmin)]
        builder.AddAdminPolicies();

        var app = builder.Build();

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
            if (dbContext.Database.GetPendingMigrations().Count() > 0)
            {
                dbContext.Database.Migrate();
            }
        }
    }
}
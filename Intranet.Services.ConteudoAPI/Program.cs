using BuildingBlocks.Shared.Logging;
using BuildingBlocks.Shared.Messaging;
using BuildingBlocks.Shared.Middleware;
using AutoMapper;
using Intranet.Services.ConteudoAPI;
using Intranet.Services.ConteudoAPI.Data;
using Intranet.Services.ConteudoAPI.Extensions;
using Intranet.Services.ConteudoAPI.Repository;
using Intranet.Services.ConteudoAPI.Repository.IRepository;
using Intranet.Services.ConteudoAPI.Services;
using Intranet.Services.ConteudoAPI.Utility;
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

        var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");

        builder.Services.AddDbContext<AppDbContext>(options =>
        {
            options.UseNpgsql(connectionString);
            // Migrações escritas à mão (sem Designer): o snapshot é mantido manualmente
            options.ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning));
        });

        builder.Services.AddScoped(typeof(IRepository<>), typeof(Repository<>));
        builder.Services.AddScoped<IUnityOfWork, UnityOfWork>();
        builder.Services.AddScoped<IPostRepository, PostRepository>();
        builder.Services.AddScoped<ICommentRepository, CommentRepository>();
        builder.Services.AddScoped<IPostLikeRepository, PostLikeRepository>();
        builder.Services.AddScoped<ICommentLikeRepository, CommentLikeRepository>();
        builder.Services.AddScoped<BackendApiAuthenticationHttpClientHandler>();
        builder.Services.AddHttpContextAccessor();

        // Mensageria (MassTransit + RabbitMQ): newsletter e notificações publicadas para o
        // NotificacaoAPI; autoria (membro do token) consultada no IdentidadeAPI
        builder.AddMessaging();
        builder.Services.AddScoped<INewsletterClient, NewsletterClient>();
        builder.Services.AddScoped<INotificationClient, NotificationClient>();
        builder.Services.AddScoped<IMemberResolver, MemberResolver>();

        // AutoMapper 13+ já inclui a integração com DI (sem AutoMapper.Extensions.Microsoft.DependencyInjection)
        builder.Services.AddAutoMapper(cfg => { }, typeof(MappingConfig));

        builder.Services.AddControllers();
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();

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
                       .WithExposedHeaders("x-pagination", "Authorization");
            });
        });

        builder.AddAppAuthentication();

        builder.Services.AddAuthorization();

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
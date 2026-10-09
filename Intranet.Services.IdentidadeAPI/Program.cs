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
using Microsoft.OpenApi;

public partial class Program
{
    private static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Add services to the container.
        var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");

        builder.Services.AddDbContext<AppDbContext>(options =>
        {
            options.UseNpgsql(connectionString);
        });

        builder.Services.AddScoped(typeof(IRepository<>), typeof(Repository<>));
        builder.Services.AddScoped<IUnityOfWork, UnityOfWork>();
        builder.Services.AddScoped<IWorkRepository, WorkRepository>();
        builder.Services.AddScoped<IEducationRepository, EducationRepository>();
        builder.Services.AddScoped<IWorkService, WorkService>();
        builder.Services.AddScoped<IEducationService, EducationService>();
        builder.Services.AddScoped<BackendApiAuthenticationHttpClientHandler>();
        builder.Services.AddHttpContextAccessor();

        builder.Services.AddHttpClient("Work", u => u.BaseAddress =
            new Uri(builder.Configuration
            ["ServicesUrls:BaseAddress"])).AddHttpMessageHandler<BackendApiAuthenticationHttpClientHandler>();

        builder.Services.AddHttpClient("Education", u => u.BaseAddress =
            new Uri(builder.Configuration
            ["ServicesUrls:BaseAddress"])).AddHttpMessageHandler<BackendApiAuthenticationHttpClientHandler>();

        // AutoMapper 13+ já inclui a integração com DI (sem AutoMapper.Extensions.Microsoft.DependencyInjection)
        builder.Services.AddAutoMapper(cfg => { }, typeof(MappingConfig));

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

        builder.Services.AddCors(options =>
        {
            options.AddPolicy("DefaultPolicy", builder =>
            {
                builder.WithOrigins("http://localhost:5173", "http://10.11.10.5:31")
                       .AllowAnyMethod()
                       .AllowAnyHeader()
                       .AllowCredentials()
                       .WithExposedHeaders("Authorization");
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
            if (dbContext.Database.GetPendingMigrations().Count() > 0)
            {
                dbContext.Database.Migrate();
            }
        }
    }
}
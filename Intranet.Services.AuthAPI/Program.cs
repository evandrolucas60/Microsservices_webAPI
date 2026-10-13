using Intranet.Services.AuthAPI.Data;
using Intranet.Services.AuthAPI.Models;
using Intranet.Services.AuthAPI.Service;
using Intranet.Services.AuthAPI.Service.IService;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.Text;

public partial class Program
{
    public const string AdminRole = "Admin";

    private static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");

        builder.Services.AddDbContext<AppDbContext>(options =>
        {
            options.UseNpgsql(connectionString);
        });

        builder.Services.AddIdentity<ApplicationUser, IdentityRole>()
            .AddEntityFrameworkStores<AppDbContext>()
            .AddDefaultTokenProviders();

        // Validação do JWT emitido por este serviço (usada nos endpoints protegidos:
        // revoke e administração de papéis). O esquema padrão continua o do Identity.
        var jwtSection = builder.Configuration.GetSection("JWT");
        builder.Services.AddAuthentication()
            .AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, x =>
            {
                x.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(
                        jwtSection.GetValue<string>("SecretKey") ?? throw new InvalidOperationException("JWT:SecretKey não configurado."))),
                    ValidateIssuer = true,
                    ValidIssuer = jwtSection.GetValue<string>("ValidIssuer"),
                    ValidateAudience = true,
                    ValidAudience = jwtSection.GetValue<string>("ValidAudience"),
                };
            });

        // Add services to the container.
        builder.Services.AddControllers();
        builder.Services.AddScoped<ITokenService, TokenService>();
        // Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();

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
        SeedAdmins().GetAwaiter().GetResult();
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

        // Garante o papel "Admin" e o atribui aos e-mails de Bootstrap:AdminEmails
        // (forma de criar o primeiro administrador, já que AddUserToRole exige um Admin).
        async Task SeedAdmins()
        {
            using var scope = app.Services.CreateScope();
            var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

            if (!await roleManager.RoleExistsAsync(AdminRole))
            {
                await roleManager.CreateAsync(new IdentityRole(AdminRole));
            }

            var emails = app.Configuration.GetSection("Bootstrap:AdminEmails").Get<string[]>() ?? Array.Empty<string>();
            foreach (var email in emails.Where(e => !string.IsNullOrWhiteSpace(e)))
            {
                var user = await userManager.FindByEmailAsync(email);
                if (user is null)
                {
                    logger.LogWarning("Bootstrap:AdminEmails: usuário {Email} não encontrado (cadastre-o e reinicie).", email);
                    continue;
                }
                if (!await userManager.IsInRoleAsync(user, AdminRole))
                {
                    await userManager.AddToRoleAsync(user, AdminRole);
                    logger.LogInformation("Usuário {Email} adicionado ao papel {Role}.", email, AdminRole);
                }
            }
        }
    }
}
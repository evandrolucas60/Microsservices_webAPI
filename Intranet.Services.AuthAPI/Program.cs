using BuildingBlocks.Shared.Messaging;
using BuildingBlocks.Shared.Logging;
using BuildingBlocks.Shared.Middleware;
using BuildingBlocks.Shared.Security;
using Intranet.Services.AuthAPI.Consumers;
using Intranet.Services.AuthAPI.Data;
using Intranet.Services.AuthAPI.Models;
using Intranet.Services.AuthAPI.Service;
using Intranet.Services.AuthAPI.Service.IService;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.IdentityModel.Tokens;
using System.Text;

public partial class Program
{
    public const string AdminRole = "Admin";
    public const string SuperAdminRole = "superAdmin";

    /// <summary>Papéis usados pela Escala 5x2 (o gateway mapeia superAdmin → Admin; os demais → Colaborador).</summary>
    public static readonly string[] Escala5x2Roles = { "Gestor", "RH", "Juridico" };

    private static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Logs estruturados
        builder.AddSharedSerilog();

        // Mensageria (MassTransit + RabbitMQ): publica os eventos de login/acesso e de restrições;
        // responde às consultas de bloqueio/restrições dos demais serviços
        builder.AddMessaging(x => x.AddConsumer<ObterRestricoesUsuarioConsumer>());

        var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");

        builder.Services.AddDbContext<AppDbContext>(options =>
        {
            options.UseNpgsql(connectionString);
            // Migrations escritas à mão: diferenças cosméticas no snapshot não devem impedir a inicialização
            options.ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
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

        // Painel de gestão (superAdmin): política e regras de usuários/papéis/restrições
        builder.AddAdminPolicies();
        builder.Services.AddScoped<AdminUsuariosService>();
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
                       .WithExposedHeaders("Authorization", "X-Pagination");
            });
        });

        var app = builder.Build();

        // Configure the HTTP request pipeline.
        if (app.Environment.IsDevelopment())
        {
            app.UseSwagger();
            app.UseSwaggerUI();
        }

        // Atrás do Intranet.Gateway (/auth/...): o IP do colaborador chega em X-Forwarded-For (registro de
        // acessos da auditoria). Só aceito de proxies conhecidos (padrão: loopback, gateway na mesma máquina).
        app.UseForwardedHeaders(new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
        });

        app.UseSharedRequestLogging();
        app.UseHttpsRedirection();
        app.UseCors("DefaultPolicy");
        app.UseSharedExceptionHandling();
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

            // Papel do painel de gestão: só é criado (não é atribuído a ninguém automaticamente)
            if (!await roleManager.RoleExistsAsync(SuperAdminRole))
            {
                await roleManager.CreateAsync(new IdentityRole(SuperAdminRole));
            }

            // Papéis da Escala 5x2 (Gestor, RH, Juridico): só são criados; a atribuição é feita
            // pelo Painel de gestão. Comparação sem diferenciar maiúsculas (nome normalizado).
            foreach (var role in Escala5x2Roles)
            {
                if (!await roleManager.RoleExistsAsync(role))
                {
                    await roleManager.CreateAsync(new IdentityRole(role));
                    logger.LogInformation("Papel {Role} criado.", role);
                }
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
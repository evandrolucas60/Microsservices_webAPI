using System.Text;
using System.Threading.RateLimiting;
using BuildingBlocks.Shared.Logging;
using BuildingBlocks.Shared.Messaging;
using BuildingBlocks.Shared.Middleware;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Yarp.ReverseProxy.Transforms;

public partial class Program
{
    // Metadado da rota (appsettings) com o prefixo público do serviço no gateway, ex.: "/conteudo"
    private const string MetadataPrefixo = "PrefixoGateway";

    // Política de rate limit do login/cadastro (AuthAPI): 10 tentativas por minuto por IP
    private const string PoliticaLogin = "login";

    // Rotas (seção ReverseProxy do appsettings):
    // - Escala 5x2: o front chama /api/escala5x2/<rota> (exige JWT no gateway);
    // - Serviços da intranet: /<prefixo>/<rota original>, ex.: /conteudo/api/post → ConteudoAPI /api/post.
    //   Sem autorização no gateway (há endpoints anônimos): quem autoriza é o próprio serviço.
    private static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Logs estruturados
        builder.AddSharedSerilog();

        // Mensageria (MassTransit + RabbitMQ): bloqueio de usuários (AuthAPI)
        builder.AddMessaging();

        // JWT da intranet (emitido pelo AuthAPI no login), validado com os mesmos parâmetros dos serviços
        var apiSettings = builder.Configuration.GetSection("ApiSettings");
        var secret = apiSettings.GetValue<string>("Secret") ?? throw new InvalidOperationException("ApiSettings:Secret não configurado.");
        builder.Services.AddAuthentication(x =>
        {
            x.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
            x.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
        }).AddJwtBearer(x =>
        {
            x.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.ASCII.GetBytes(secret)),
                ValidateIssuer = true,
                ValidIssuer = apiSettings.GetValue<string>("Issuer"),
                ValidateAudience = true,
                ValidAudience = apiSettings.GetValue<string>("Audience"),
            };
            x.Events = new JwtBearerEvents
            {
                // WebSocket do chat: o navegador não envia cabeçalhos, o token vem em ?access_token=
                // (assim o bloqueio de usuário também vale no gateway; o ComunicacaoAPI valida de novo)
                OnMessageReceived = context =>
                {
                    var token = context.Request.Query["access_token"].ToString();
                    if (!string.IsNullOrEmpty(token) && context.HttpContext.Request.Path.StartsWithSegments("/comunicacao/ws/chat"))
                    {
                        context.Token = token;
                    }
                    return Task.CompletedTask;
                },
            };
        });
        builder.Services.AddAuthorization();

        // Rate limit do login e do cadastro (rotas com "RateLimiterPolicy": "login"), por IP.
        // Configurável em RateLimit:Login (PermitLimit/WindowSeconds): aumente se muitos colaboradores
        // chegarem ao gateway pelo mesmo IP (NAT/proxy) e passarem a receber 429.
        var limiteLogin = builder.Configuration.GetSection("RateLimit:Login");
        var tentativasLogin = Math.Max(limiteLogin.GetValue("PermitLimit", 30), 1);
        var janelaLogin = TimeSpan.FromSeconds(Math.Max(limiteLogin.GetValue("WindowSeconds", 60), 1));
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(PoliticaLogin, context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "desconhecido",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = tentativasLogin,
                    Window = janelaLogin,
                    QueueLimit = 0,
                }));
            options.OnRejected = async (context, token) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter = ((int)retryAfter.TotalSeconds).ToString();
                }
                await context.HttpContext.Response.WriteAsJsonAsync(new
                {
                    isSuccess = false,
                    message = "Muitas tentativas. Aguarde um minuto e tente novamente.",
                }, token);
            };
        });

        // Proxy reverso: rotas e destinos na seção "ReverseProxy". O Authorization (JWT da intranet) é
        // repassado como veio: cada serviço valida o mesmo token e aplica as próprias regras.
        builder.Services.AddReverseProxy()
            .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"))
            .AddTransforms(context =>
            {
                // Nunca repassar a chave interna (/internal entre serviços) vinda de fora
                context.AddRequestHeaderRemove("X-Internal-Key");

                // CORS é respondido só pelo gateway (política "DefaultPolicy"): descarta os cabeçalhos
                // CORS que os serviços também enviam, para o navegador não receber valores duplicados
                context.AddResponseTransform(transform =>
                {
                    RemoverCorsDoServico(transform.HttpContext.Response.Headers);
                    return ValueTask.CompletedTask;
                });

                // Serviços da intranet: informa o endereço público (gateway + prefixo) ao serviço e
                // corrige redirecionamentos/Location que apontem para o endereço interno do serviço
                if (context.Route.Metadata?.TryGetValue(MetadataPrefixo, out var prefixo) == true && !string.IsNullOrEmpty(prefixo))
                {
                    // X-Forwarded-For/Proto/Host como o padrão do YARP; X-Forwarded-Prefix = prefixo removido
                    context.AddXForwarded(ForwardedTransformActions.Set);
                    context.AddRequestHeader("X-Forwarded-Prefix", prefixo, append: false);
                    context.AddResponseTransform(transform =>
                    {
                        AjustarLocation(transform, prefixo);
                        return ValueTask.CompletedTask;
                    });
                }
            });

        // Portal (Vite em desenvolvimento e servidor interno). O gateway é a única origem que o navegador vê:
        // expõe a união dos cabeçalhos que os serviços expunham (o front lê X-Pagination e Content-Disposition)
        builder.Services.AddCors(options =>
        {
            options.AddPolicy("DefaultPolicy", policy =>
            {
                policy.WithOrigins(builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
                        ?? new[] { "http://localhost:5173", "http://10.11.10.5:31" })
                      .AllowAnyMethod()
                      .AllowAnyHeader()
                      .AllowCredentials()
                      .WithExposedHeaders("Location", "Content-Disposition", "X-Correlation-Id", "X-Pagination", "Authorization", "Retry-After");
            });
        });

        var app = builder.Build();

        app.UseSharedRequestLogging();
        app.UseHttpsRedirection();
        app.UseCors("DefaultPolicy");
        app.UseSharedExceptionHandling();
        app.UseAuthentication();
        // Usuário bloqueado pelo superAdmin → 403 (consulta o AuthAPI pelo RabbitMQ, com cache).
        // Só age quando há token válido; requisições anônimas seguem para o serviço.
        app.UseSharedUserAccess();
        app.UseAuthorization();
        app.UseRateLimiter();

        // GET /api/escala5x2/me é roteado ao Store.Api (/api/me), como as demais rotas da Escala 5x2
        app.MapReverseProxy();
        app.Run();
    }

    /// <summary>Remove Access-Control-* (e o "Vary: Origin") vindos do serviço; o CORS do gateway os define.</summary>
    private static void RemoverCorsDoServico(IHeaderDictionary headers)
    {
        foreach (var nome in headers.Keys.Where(k => k.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            headers.Remove(nome);
        }

        if (headers.Vary.Count > 0)
        {
            var vary = headers.Vary.SelectMany(v => (v ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                .Where(v => !v.Equals("Origin", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (vary.Length == 0) headers.Remove("Vary");
            else headers.Vary = string.Join(", ", vary);
        }
    }

    /// <summary>
    /// Location relativo ("/api/...") ou absoluto com o endereço interno do serviço (https://localhost:70xx)
    /// passa a apontar para o gateway com o prefixo do serviço.
    /// </summary>
    private static void AjustarLocation(ResponseTransformContext transform, string prefixo)
    {
        var location = transform.ProxyResponse?.Headers.Location;
        if (location is null) return;

        var request = transform.HttpContext.Request;
        var publico = $"{request.Scheme}://{request.Host}{prefixo}";
        string? novo = null;

        if (!location.IsAbsoluteUri)
        {
            if (location.OriginalString.StartsWith('/')) novo = publico + location.OriginalString;
        }
        else if (transform.ProxyResponse!.RequestMessage?.RequestUri is { } destino
                 && Uri.Compare(location, destino, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0)
        {
            novo = publico + location.PathAndQuery;
        }

        if (novo is not null) transform.HttpContext.Response.Headers.Location = novo;
    }
}

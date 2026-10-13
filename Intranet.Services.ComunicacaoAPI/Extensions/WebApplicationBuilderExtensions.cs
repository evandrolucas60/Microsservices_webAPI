using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;

namespace Intranet.Services.ComunicacaoAPI.Extensions;

public static class WebApplicationBuilderExtensions
{
    // Mesma validação de JWT dos demais serviços (tokens emitidos pelo AuthAPI)
    public static WebApplicationBuilder AddAppAuthentication(this WebApplicationBuilder builder)
    {
        var settingsSection = builder.Configuration.GetSection("ApiSettings");

        var secret = settingsSection.GetValue<string>("Secret")
            ?? throw new InvalidOperationException("ApiSettings:Secret não configurado.");
        var issuer = settingsSection.GetValue<string>("Issuer");
        var audience = settingsSection.GetValue<string>("Audience");

        var key = Encoding.ASCII.GetBytes(secret);

        builder.Services.AddAuthentication(x =>
        {
            x.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
            x.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
        }).AddJwtBearer(x =>
        {
            x.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(key),
                ValidateIssuer = true,
                ValidIssuer = issuer,
                ValidAudience = audience,
                ValidateAudience = true,
            };
            // WebSocket do chat: o navegador não envia o cabeçalho Authorization, o token vem na query
            x.Events = new JwtBearerEvents
            {
                OnMessageReceived = context =>
                {
                    var token = context.Request.Query["access_token"].ToString();
                    if (!string.IsNullOrEmpty(token) && context.HttpContext.Request.Path.StartsWithSegments("/ws/chat"))
                    {
                        context.Token = token;
                    }
                    return Task.CompletedTask;
                },
            };
        });

        return builder;
    }
}

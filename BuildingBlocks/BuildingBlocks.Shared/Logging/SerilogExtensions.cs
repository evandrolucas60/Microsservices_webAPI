using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Events;

namespace BuildingBlocks.Shared.Logging;

public static class SerilogExtensions
{
    /// <summary>
    /// Serilog em todos os serviços: lê a seção "Serilog" do appsettings e, quando não houver
    /// sinks configurados, grava no console e em arquivo diário (logs/{serviço}-AAAAMMDD.log, 14 dias).
    /// Todo log leva Application, MachineName e o CorrelationId da requisição.
    /// </summary>
    public static WebApplicationBuilder AddSharedSerilog(this WebApplicationBuilder builder)
    {
        var application = builder.Environment.ApplicationName;
        var logPath = Path.Combine(builder.Environment.ContentRootPath, "logs", $"{application}-.log");

        builder.Host.UseSerilog((context, services, config) =>
        {
            config
                .ReadFrom.Configuration(context.Configuration)
                .ReadFrom.Services(services)
                .Enrich.FromLogContext()
                .Enrich.WithMachineName()
                .Enrich.WithProperty("Application", application);

            if (!context.Configuration.GetSection("Serilog:MinimumLevel").Exists())
            {
                config.MinimumLevel.Information()
                    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
                    .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
                    .MinimumLevel.Override("System.Net.Http.HttpClient", LogEventLevel.Warning)
                    .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information);
            }

            if (!context.Configuration.GetSection("Serilog:WriteTo").Exists())
            {
                const string template = "[{Timestamp:HH:mm:ss} {Level:u3}] {Application} {CorrelationId} {SourceContext}: {Message:lj}{NewLine}{Exception}";
                config
                    .WriteTo.Console(outputTemplate: template)
                    .WriteTo.File(
                        logPath,
                        rollingInterval: RollingInterval.Day,
                        retainedFileCountLimit: 14,
                        fileSizeLimitBytes: 50 * 1024 * 1024,
                        rollOnFileSizeLimit: true,
                        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Application} {CorrelationId} {SourceContext}: {Message:lj}{NewLine}{Exception}");
            }
        });

        return builder;
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Escala5x2.BuildingBlocks.Shared.Persistence;

public static class PersistenceExtensions
{
    /// <summary>Registra o DbContext do serviço apontando para o seu próprio banco lógico no PostgreSQL.</summary>
    /// <param name="splitQueries">
    /// Include de coleções em consultas separadas (QuerySplittingBehavior.SplitQuery): evita repetir colunas grandes
    /// (jsonb) do pai em cada linha da coleção (explosão cartesiana).
    /// </param>
    public static IServiceCollection AddPostgres<TContext>(
        this IServiceCollection services, IConfiguration configuration, bool retryOnFailure = true, bool splitQueries = false)
        where TContext : DbContext
    {
        var connectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("ConnectionStrings:Default não configurada.");

        services.AddDbContext<TContext>(o => o.UseNpgsql(connectionString, npg =>
        {
            if (retryOnFailure)
            {
                npg.EnableRetryOnFailure(5);
            }
            if (splitQueries)
            {
                npg.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
            }
        }));

        return services;
    }

    /// <summary>
    /// Atualiza o banco na inicialização, no mesmo princípio dos serviços da intranet (ApplyMigrations):
    /// havendo migrations pendentes, aplica com Migrate(). Tenta de novo enquanto o PostgreSQL não responde.
    /// </summary>
    public static async Task ApplyMigrationsAsync<TContext>(this IServiceProvider services, CancellationToken ct = default)
        where TContext : DbContext
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Database");

        const int maxAttempts = 10;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await BaselineEnsureCreatedAsync(db, logger, ct);

                var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
                if (pending.Count > 0)
                {
                    logger.LogInformation("Aplicando {Count} migration(s) em {Database}: {Migrations}",
                        pending.Count, db.Database.GetDbConnection().Database, string.Join(", ", pending));
                    await db.Database.MigrateAsync(ct);
                }

                logger.LogInformation("Banco {Database} atualizado", db.Database.GetDbConnection().Database);
                return;
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                logger.LogWarning(ex, "PostgreSQL indisponível (tentativa {Attempt}/{Max})", attempt, maxAttempts);
                await Task.Delay(TimeSpan.FromSeconds(attempt * 3), ct);
            }
        }
    }

    /// <summary>
    /// Bancos criados antes das migrations (EnsureCreated) já têm as tabelas, mas não o histórico do EF:
    /// registra a primeira migration como aplicada para o Migrate() não tentar recriá-las.
    /// Só age quando o banco existe, tem tabelas do modelo e nenhuma migration aplicada
    /// (sem __EFMigrationsHistory ou com ela vazia).
    /// </summary>
    private static async Task BaselineEnsureCreatedAsync(DbContext db, ILogger logger, CancellationToken ct)
    {
        var migrations = db.Database.GetMigrations().ToList();
        if (migrations.Count == 0 || !await db.Database.CanConnectAsync(ct)) return;

        var history = db.GetService<IHistoryRepository>();
        if (await history.ExistsAsync(ct) && (await history.GetAppliedMigrationsAsync(ct)).Count > 0) return;

        // Alguma tabela do modelo já existe? (banco criado pelo EnsureCreated)
        var tables = db.Model.GetEntityTypes()
            .Select(e => e.GetTableName())
            .Where(t => t is not null)
            .Distinct()
            .ToArray();
        var connection = db.Database.GetDbConnection();
        int existentes;
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = current_schema() AND table_name::text = ANY(@nomes)";
            var p = cmd.CreateParameter();
            p.ParameterName = "nomes";
            p.Value = tables;
            cmd.Parameters.Add(p);
            existentes = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }

        logger.LogInformation("Banco {Database} sem migrations aplicadas: {Existentes} de {Total} tabelas do modelo já existem",
            connection.Database, existentes, tables.Length);
        if (existentes == 0) return;

        var inicial = migrations[0];
        await db.Database.ExecuteSqlRawAsync(history.GetCreateIfNotExistsScript(), ct);
        await db.Database.ExecuteSqlRawAsync(
            history.GetInsertScript(new HistoryRow(inicial, ProductInfo.GetVersion())), ct);
        logger.LogWarning(
            "Banco {Database} criado sem migrations ({Tabelas} tabelas existentes): {Migration} registrada como aplicada",
            connection.Database, existentes, inicial);
    }
}

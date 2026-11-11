using BuildingBlocks.Contracts.Administracao;
using BuildingBlocks.Shared.Security;
using MassTransit;
using Microsoft.AspNetCore.Identity;

namespace Intranet.Services.AuthAPI.Consumers;

/// <summary>
/// Comando (RabbitMQ) <see cref="GarantirRoles"/>: cria os papéis que faltam (regra em <see cref="GarantidorDeRoles"/>).
/// Usado pela Escala 5x2 para as roles por loja ("Escala5x2-Loja-01"...), que o superAdmin atribui no painel de gestão.
/// Publicado (sem resposta) a cada loja criada/sincronizada; enviado como requisição pelo "Garantir roles das lojas"
/// (responde <see cref="RolesGarantidas"/>). Idempotente; não apaga papel (roles de loja inativa continuam existindo).
/// </summary>
public class GarantirRolesConsumer : IConsumer<GarantirRoles>
{
    private readonly RoleManager<IdentityRole> _roleManager;
    private readonly ILogger<GarantirRolesConsumer> _logger;

    public GarantirRolesConsumer(RoleManager<IdentityRole> roleManager, ILogger<GarantirRolesConsumer> logger)
    {
        _roleManager = roleManager;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<GarantirRoles> context)
    {
        var resultado = await GarantidorDeRoles.GarantirAsync(context.Message.Nomes, new ArmazemIdentity(_roleManager), context.CancellationToken);

        if (resultado.Criadas.Length > 0)
            _logger.LogInformation("Papéis criados a pedido de {Origem}: {Roles}", context.Message.Origem ?? "(não informado)", string.Join(", ", resultado.Criadas));
        if (resultado.Recusadas.Length > 0)
            _logger.LogWarning("Papéis recusados a pedido de {Origem}: {Recusadas}", context.Message.Origem ?? "(não informado)", string.Join("; ", resultado.Recusadas));

        // Só responde quando veio como requisição (o publish das lojas não espera resposta)
        if (context.ResponseAddress is not null) await context.RespondAsync(resultado);
    }

    /// <summary>Papéis no Identity (RoleExistsAsync compara pelo nome normalizado).</summary>
    private sealed class ArmazemIdentity(RoleManager<IdentityRole> roleManager) : IArmazemRoles
    {
        public Task<bool> ExisteAsync(string nome, CancellationToken ct) => roleManager.RoleExistsAsync(nome);

        public async Task<(CriacaoRole Resultado, string? Erro)> CriarAsync(string nome, CancellationToken ct)
        {
            var result = await roleManager.CreateAsync(new IdentityRole(nome));
            if (result.Succeeded) return (CriacaoRole.Criada, null);
            // Corrida com outro pedido igual: o RoleValidator acusa nome duplicado
            if (result.Errors.Any(e => e.Code == nameof(IdentityErrorDescriber.DuplicateRoleName))) return (CriacaoRole.JaExistia, null);
            return (CriacaoRole.Falhou, string.Join(" ", result.Errors.Select(e => e.Description)));
        }
    }
}

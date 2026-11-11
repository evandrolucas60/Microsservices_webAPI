using Escala5x2.BuildingBlocks.Contracts.Stores;
using Escala5x2.BuildingBlocks.Shared.Auth;
using Escala5x2.BuildingBlocks.Shared.Web;
using Store.Api.Lg;

namespace Store.Api.Endpoints;

/// <summary>Pedido interno do Employee.Api (sincronizar lojas antes dos colaboradores). <paramref name="Por"/>: quem iniciou.</summary>
public sealed record SincronizarLojasInternoRequest(string? Por);

/// <summary>Sincronização de lojas pelos centros de custo da LG e dos setores pelo catálogo de órgãos responsáveis.</summary>
public static class LgLojasEndpoints
{
    public static void MapLgLojasEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/stores").RequireAuthorization();

        // Executar e ver o status/resumo das sincronizações (centros de custo ignorados, conflitos...): só superAdmin
        // (Admin) e RH (Policies.LgSync), como a dos colaboradores no Employee.Api.

        // Em segundo plano: 202 + status; 409 já em andamento; 503 sem configuração
        group.MapPost("/sincronizar-lg", (LgLojasRunner runner, ICurrentUser user) =>
        {
            if (runner.TryIniciar(user.Display) is { } recusa)
                return runner.Status() is { Configurada: false }
                    ? Results.Problem(title: "Integração LG não configurada", detail: recusa, statusCode: StatusCodes.Status503ServiceUnavailable)
                    : Results.Problem(title: "Sincronização em andamento", detail: recusa, statusCode: StatusCodes.Status409Conflict);
            return Results.Accepted("/api/stores/sincronizar-lg/status", runner.Status());
        }).RequireAuthorization(Policies.LgSync);

        group.MapGet("/sincronizar-lg/status", (LgLojasRunner runner) => Results.Ok(runner.Status()))
            .RequireAuthorization(Policies.LgSync);

        // Catálogo de setores (órgãos responsáveis da LG) em todas as lojas ativas, em segundo plano: 202 + status;
        // 409 em andamento; 503 sem configuração. Também roda logo depois de cada sincronização de lojas.
        group.MapPost("/sincronizar-setores-lg", (LgSetoresRunner runner, ICurrentUser user) =>
        {
            if (runner.TryIniciar(user.Display) is { } recusa)
                return runner.Status() is { Configurada: false }
                    ? Results.Problem(title: "Integração LG não configurada", detail: recusa, statusCode: StatusCodes.Status503ServiceUnavailable)
                    : Results.Problem(title: "Sincronização em andamento", detail: recusa, statusCode: StatusCodes.Status409Conflict);
            return Results.Accepted("/api/stores/sincronizar-setores-lg/status", runner.Status());
        }).RequireAuthorization(Policies.LgSync);

        group.MapGet("/sincronizar-setores-lg/status", (LgSetoresRunner runner) => Results.Ok(runner.Status()))
            .RequireAuthorization(Policies.LgSync);

        // Interno (X-Internal-Key): o Employee.Api sincroniza as lojas antes dos colaboradores e aguarda o resumo
        var internalApi = app.MapInternalGroup();
        internalApi.MapPost("/stores/sincronizar-lg", async (SincronizarLojasInternoRequest? req, LgLojasRunner runner, CancellationToken ct) =>
        {
            var status = runner.Status();
            if (!status.Configurada)
                return Results.Problem(title: "Integração LG não configurada", detail: status.MensagemConfiguracao,
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            var por = string.IsNullOrWhiteSpace(req?.Por) ? "Employee.Api" : req.Por.Trim();
            var resumo = await runner.ExecutarAsync($"{por} (antes dos colaboradores)", ct);
            return resumo is null
                ? Results.Problem(title: "Sincronização em andamento", detail: "Já existe uma sincronização de lojas em andamento.",
                    statusCode: StatusCodes.Status409Conflict)
                : Results.Ok(resumo);
        });

        // Interno: o Employee.Api garante os setores dos órgãos responsáveis da LG de cada loja ANTES de gravar os
        // colaboradores (uma chamada por loja) e recebe código do órgão → SectorId. Ver LgSetoresRegras.
        internalApi.MapPost("/stores/{storeId:guid}/sectors/garantir-lg", async (Guid storeId, GarantirSetoresLgRequest req,
            LgSetoresService setores, CancellationToken ct) =>
        {
            if (req.Orgaos is null) return ServiceDefaults.ValidationError("orgaos", "Informe a lista de órgãos (pode ser vazia).");
            return await setores.GarantirAsync(storeId, req, ct) is { } r ? Results.Ok(r) : Results.NotFound();
        });
    }
}

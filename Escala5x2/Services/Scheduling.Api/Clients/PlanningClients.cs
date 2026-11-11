using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Escala5x2.BuildingBlocks.Contracts.Compliance;
using Escala5x2.BuildingBlocks.Contracts.Http;
using Escala5x2.BuildingBlocks.Contracts.Planning;

namespace Scheduling.Api.Clients;

public static class Json
{
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        o.Converters.Add(new JsonStringEnumConverter());
        return o;
    }
}

/// <summary>Chamadas HTTP aos endpoints /internal dos outros serviços. Nunca acessa o banco deles.</summary>
public sealed class PlanningClients(IHttpClientFactory factory, IConfiguration cfg, ILogger<PlanningClients> logger)
{
    public const string Employee = "employee";
    public const string Store = "store";
    public const string Compliance = "compliance";

    public Task<StorePlanningResponse?> GetStorePlanningAsync(Guid storeId, DateOnly from, DateOnly to, CancellationToken ct) =>
        factory.CreateClient(Store).GetFromJsonAsync<StorePlanningResponse>(
            $"/internal/stores/{storeId}/planning?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}", Json.Options, ct);

    public async Task<IReadOnlyList<EmployeeSnapshot>> GetEmployeesAsync(Guid storeId, DateOnly from, DateOnly to, CancellationToken ct) =>
        await factory.CreateClient(Employee).GetFromJsonAsync<List<EmployeeSnapshot>>(
            $"/internal/planning/employees?storeId={storeId}&from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}", Json.Options, ct) ?? [];

    /// <summary>Ausências ATUAIS (um registro por pessoa e dia, com o tipo) das pessoas da loja no período.</summary>
    public async Task<IReadOnlyList<AbsenceEntry>> GetAbsencesAsync(Guid storeId, DateOnly from, DateOnly to, CancellationToken ct) =>
        await factory.CreateClient(Employee).GetFromJsonAsync<List<AbsenceEntry>>(
            $"/internal/planning/absences?storeId={storeId}&from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}", Json.Options, ct) ?? [];

    /// <summary>
    /// Colaboradores em férias/afastamento na LG sem período registrado (fora de <see cref="GetEmployeesAsync"/>).
    /// Employee.Api antigo ou fora do ar: lista vazia (é só aviso).
    /// </summary>
    public async Task<IReadOnlyList<PendingPeriodEntry>> GetPendingAsync(Guid storeId, CancellationToken ct)
    {
        try
        {
            return await factory.CreateClient(Employee).GetFromJsonAsync<List<PendingPeriodEntry>>(
                $"/internal/planning/pending?storeId={storeId}", Json.Options, ct) ?? [];
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return [];
        }
    }

    /// <summary>
    /// Atualiza na LG as férias dos colaboradores do setor (ou só <paramref name="employeeIds"/>) antes de montar/gerar a
    /// fotografia (POST /internal/planning/ferias-lg). Espera no máximo Scheduling:FeriasLgTimeoutSegundos (padrão 20; 0 =
    /// não atualiza): o Employee.Api devolve "EmAndamento" quando a LG demora. Devolve o aviso da geração (null = nada a
    /// avisar). Nunca lança nem bloqueia: com o Employee.Api/LG fora, a geração segue com as férias já gravadas.
    /// </summary>
    public async Task<string?> RefreshLgVacationsAsync(Guid storeId, Guid? sectorId, IReadOnlyList<Guid>? employeeIds, CancellationToken ct)
    {
        var seconds = cfg.GetValue("Scheduling:FeriasLgTimeoutSegundos", 20);
        if (seconds <= 0) return null;
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(seconds + 10));
            using var response = await factory.CreateClient(Employee).PostAsJsonAsync("/internal/planning/ferias-lg",
                new FeriasLgAtualizacaoRequest(storeId, sectorId, employeeIds, seconds), Json.Options, limit.Token);
            // Employee.Api anterior a esta integração: nada a atualizar nem a avisar
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<FeriasLgAtualizacaoResponse>(Json.Options, limit.Token);
            if (result is not null && result.Status != FeriasLgStatus.NaoConfigurada)
                logger.LogInformation("Férias da LG para a geração (loja {StoreId}, setor {SectorId}): {Status}, {Consultados}/{Colaboradores} consultado(s), {Falhas} falha(s), {Alterados} com período alterado",
                    storeId, sectorId, result.Status, result.Consultados, result.Colaboradores, result.Falhas, result.Alterados);
            return FeriasLgStatus.Aviso(result);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Férias da LG não atualizadas antes da geração (loja {StoreId}): {Motivo}; seguindo com as já registradas",
                storeId, $"{ex.GetType().Name}: {ex.Message}");
            return FeriasLgStatus.Aviso(null);
        }
    }

    /// <summary>Linhas do cadastro de regras de conformidade (ativas e bloqueadas) com vigência no horizonte.</summary>
    public async Task<IReadOnlyList<ComplianceRuleSnapshot>> GetRulesAsync(DateOnly from, DateOnly to, CancellationToken ct) =>
        await factory.CreateClient(Compliance).GetFromJsonAsync<List<ComplianceRuleSnapshot>>(
            $"/internal/rules?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}", Json.Options, ct) ?? [];
}

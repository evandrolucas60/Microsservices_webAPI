using Escala5x2.BuildingBlocks.Compliance;
using Escala5x2.BuildingBlocks.Contracts.Scheduling;
using MassTransit;
using Optimization.Worker.Engine;

namespace Optimization.Worker.Consumers;

/// <summary>
/// Gera a escala. Geração cancelada (<see cref="CancelScheduleGeneration"/>) antes de começar é descartada (o comando
/// ainda estava na fila); durante o cálculo o CP-SAT é interrompido. Em ambos os casos nenhum resultado é publicado — a
/// escala já voltou ao estado anterior no Scheduling.Api.
/// </summary>
public sealed class GenerateScheduleConsumer(
    ScheduleSolver solver, ComplianceEvaluator evaluator, GenerationRegistry registry, ILogger<GenerateScheduleConsumer> logger)
    : IConsumer<GenerateScheduleCommand>
{
    public async Task Consume(ConsumeContext<GenerateScheduleCommand> context)
    {
        var cmd = context.Message;
        // Nome completo: MassTransit também tem um tipo LogContext.
        using var _ = Serilog.Context.LogContext.PushProperty("ScheduleId", cmd.ScheduleId);
        if (registry.IsCancelled(cmd.ScheduleId, cmd.GenerationId))
        {
            logger.LogInformation("Geração {GenerationId} da escala {ScheduleId} descartada: cancelada antes de começar", cmd.GenerationId, cmd.ScheduleId);
            return;
        }
        logger.LogInformation("Gerando escala {ScheduleId} v{Version}: {Start} a {End} ({Weeks} semana(s) civis), modo {Mode}, {Fixed} célula(s) manual(is)",
            cmd.ScheduleId, cmd.Version, cmd.StartDate, cmd.LastDate, cmd.Weeks, cmd.Mode, cmd.FixedCells?.Count ?? 0);

        using var lease = registry.Start(cmd.ScheduleId, cmd.GenerationId, context.CancellationToken);
        try
        {
            // CP-SAT é síncrono e usa várias threads; roda fora do thread do consumidor.
            var result = await Task.Run(() => solver.Solve(cmd, ct: lease.Token), lease.Token);
            if (lease.Token.IsCancellationRequested && !context.CancellationToken.IsCancellationRequested)
            {
                logger.LogInformation("Geração {GenerationId} da escala {ScheduleId} cancelada: resultado descartado", cmd.GenerationId, cmd.ScheduleId);
                return;
            }
            foreach (var w in result.Warnings) logger.LogWarning("Aviso: {Warning}", w);

            switch (result)
            {
                case SolveResult.Success ok:
                    // Conferência independente: nenhuma regra rígida (nem mínimo de cobertura) pode estar violada — não
                    // há modo flexível; violação aqui é defeito do modelo e a escala NÃO é entregue (Falha com o detalhe)
                    var evaluation = evaluator.Evaluate(ok.Ctx, ok.Assignments);
                    var rigid = ComplianceEvaluator.RigidViolations(evaluation);
                    if (rigid > 0)
                    {
                        var rigidViolations = evaluation.Violacoes.Where(v => v.Severidade is "alta" or "media").ToList();
                        foreach (var v in rigidViolations)
                        {
                            logger.LogError("Violação {Rule}: {Detail}", v.Chave, v.Detalhe);
                            await context.Publish(new ScheduleViolationDetected(cmd.ScheduleId, v.Chave, v.Detalhe, DateTime.UtcNow));
                        }
                        await context.Publish(new ScheduleGenerationFailed(cmd.ScheduleId,
                            $"O avaliador independente encontrou {rigid} violação(ões) de regra rígida na escala do motor; a escala foi descartada. " +
                            $"Primeira: {rigidViolations[0].Detalhe}", DateTime.UtcNow, cmd.GenerationId));
                        return;
                    }

                    await context.Publish(new ScheduleGenerated(cmd.ScheduleId, ok.Assignments, ok.Objective, ok.Seconds, ok.Status, DateTime.UtcNow,
                        false, ok.Notice, ok.Deficits, ok.Status, ok.Solutions, cmd.GenerationId, ok.Ctx.CoverageTargetSectorIds(),
                        ok.Ctx.CoverageTargetDayList()));
                    logger.LogInformation("Escala {ScheduleId} gerada ({Result}): {Count} alocações, {Seconds:F1}s, conformidade {Percent}%",
                        cmd.ScheduleId, ok.Status, ok.Assignments.Count, ok.Seconds, evaluation.Percentual);
                    break;

                case SolveResult.Infeasible bad:
                    await context.Publish(new ScheduleInfeasible(cmd.ScheduleId, bad.Reason, bad.Deficits, DateTime.UtcNow, bad.Diagnosis, cmd.GenerationId));
                    logger.LogWarning("Escala {ScheduleId} inviável: {Reason}", cmd.ScheduleId, bad.Reason);
                    break;

                case SolveResult.TimedOut slow:
                    await context.Publish(new ScheduleNoSolutionInTime(cmd.ScheduleId, slow.Reason, slow.TimeLimitSeconds, slow.Seconds, DateTime.UtcNow,
                        cmd.GenerationId));
                    logger.LogWarning("Escala {ScheduleId} sem solução no tempo ({Seconds}s)", cmd.ScheduleId, slow.TimeLimitSeconds);
                    break;
            }
        }
        catch (OperationCanceledException) when (lease.Token.IsCancellationRequested && !context.CancellationToken.IsCancellationRequested)
        {
            // Cancelada pelo gestor/Admin: o CP-SAT parou; nada é publicado (a escala já voltou ao estado anterior)
            logger.LogInformation("Geração {GenerationId} da escala {ScheduleId} cancelada: CP-SAT interrompido", cmd.GenerationId, cmd.ScheduleId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Erro de dados ou do motor: não adianta repetir a mensagem.
            logger.LogError(ex, "Falha ao gerar a escala {ScheduleId}", cmd.ScheduleId);
            await context.Publish(new ScheduleGenerationFailed(cmd.ScheduleId, ex.Message, DateTime.UtcNow, cmd.GenerationId));
        }
    }
}

/// <summary>
/// Cancelamento de geração (fila temporária desta réplica: todas as réplicas recebem). Guarda o cancelamento e, se a
/// geração está calculando aqui, interrompe o CP-SAT.
/// </summary>
public sealed class CancelScheduleGenerationConsumer(GenerationRegistry registry, ILogger<CancelScheduleGenerationConsumer> logger)
    : IConsumer<CancelScheduleGeneration>
{
    public Task Consume(ConsumeContext<CancelScheduleGeneration> context)
    {
        var msg = context.Message;
        var running = registry.Cancel(msg.ScheduleId, msg.GenerationId);
        logger.LogInformation(running
                ? "Cancelamento da geração {GenerationId} da escala {ScheduleId} por {By}: CP-SAT interrompido nesta réplica"
                : "Cancelamento da geração {GenerationId} da escala {ScheduleId} por {By} registrado (não está calculando nesta réplica)",
            msg.GenerationId, msg.ScheduleId, msg.By);
        return Task.CompletedTask;
    }
}

/// <summary>Sugestão de correção (mudança mínima). Sempre responde: pronta ou sem_solucao.</summary>
public sealed class SuggestCorrectionsConsumer(ScheduleSolver solver, ILogger<SuggestCorrectionsConsumer> logger) : IConsumer<SuggestCorrectionsCommand>
{
    public async Task Consume(ConsumeContext<SuggestCorrectionsCommand> context)
    {
        var cmd = context.Message;
        using var _ = Serilog.Context.LogContext.PushProperty("ScheduleId", cmd.ScheduleId);
        logger.LogInformation("Sugestão {SuggestionId} para a escala {ScheduleId}: {Cells} célula(s)", cmd.SuggestionId, cmd.ScheduleId, cmd.Cells.Count);
        SuggestionOutcome outcome;
        try
        {
            outcome = await Task.Run(() => solver.Suggest(cmd), context.CancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Falha ao calcular a sugestão {SuggestionId}", cmd.SuggestionId);
            outcome = new SuggestionOutcome(SuggestionStatus.SemSolucao, 0, 0, [], $"Falha no motor: {ex.Message}");
        }
        logger.LogInformation("Sugestão {SuggestionId}: {Status}, {Changes} alteração(ões), {Before}% → {After}%",
            cmd.SuggestionId, outcome.Status, outcome.Changes.Count, outcome.PercentualAtual, outcome.PercentualPrevisto);
        await context.Publish(new CorrectionSuggested(cmd.ScheduleId, cmd.SuggestionId, outcome.Status, outcome.PercentualAtual,
            outcome.PercentualPrevisto, outcome.Changes, outcome.Message, DateTime.UtcNow));
    }
}

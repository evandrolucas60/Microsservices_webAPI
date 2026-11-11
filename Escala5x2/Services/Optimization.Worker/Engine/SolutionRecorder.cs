using Escala5x2.BuildingBlocks.Contracts.Scheduling;
using Google.OrTools.Sat;

namespace Optimization.Worker.Engine;

/// <summary>
/// Registro das soluções da geração (CpSolverSolutionCallback): a cada solução melhorada que o CP-SAT encontra, guarda o
/// número, o tempo, o objetivo, o melhor limite/gap, o detalhamento do objetivo por componente e os indicadores por pessoa
/// (aberturas/fechamentos/domingos mín–máx). Só observa: nunca para a busca nem muda a escolha. O CP-SAT chama o callback
/// de forma serializada (mesmo com vários workers); o custo é avaliar as expressões dos componentes, por isso só as
/// últimas <c>max</c> soluções são guardadas (o total é sempre contado).
/// </summary>
public sealed class SolutionRecorder(ScheduleModel model, int max) : CpSolverSolutionCallback
{
    private readonly Queue<EngineSolutionDto> _kept = new();

    /// <summary>Soluções encontradas (todas, mesmo além do limite guardado).</summary>
    public int Count { get; private set; }

    public IReadOnlyCollection<EngineSolutionDto> Kept => _kept;

    public override void OnSolutionCallback()
    {
        Count++;
        if (max <= 0) return;
        try
        {
            _kept.Enqueue(Read(Count, WallTime(), ObjectiveValue(), BestObjectiveBound(), Value));
            while (_kept.Count > max) _kept.Dequeue();
        }
        catch
        {
            // O registro nunca pode derrubar a geração (exceção dentro do callback nativo encerraria o processo)
        }
    }

    /// <summary>Lê uma solução (do callback ou, no fim, do próprio solver).</summary>
    internal EngineSolutionDto Read(int number, double seconds, double objective, double bound, Func<LinearExpr, long> value, bool chosen = false)
    {
        var components = new Dictionary<string, long>(model.ObjectiveComponents.Count);
        foreach (var key in EngineComponents.All)
            if (model.ObjectiveComponents.TryGetValue(key, out var expr)) components[key] = value(expr);
        var indicators = new Dictionary<string, EngineRangeDto>(model.Indicators.Count);
        foreach (var (key, counts) in model.Indicators)
        {
            if (counts.Count == 0) continue;
            var min = int.MaxValue;
            var maxValue = int.MinValue;
            foreach (var c in counts)
            {
                var v = (int)value(c);
                if (v < min) min = v;
                if (v > maxValue) maxValue = v;
            }
            indicators[key] = new EngineRangeDto(min, maxValue);
        }
        var obj = (long)Math.Round(objective);
        return new EngineSolutionDto(number, Math.Round(seconds, 3), obj, bound, Gap(objective, bound), components, indicators, chosen);
    }

    internal static double Gap(double objective, double bound) =>
        Math.Round(Math.Max(0, objective - bound) / Math.Max(1, Math.Abs(objective)), 6);

    /// <summary>
    /// Resumo final: as soluções guardadas com a última marcada como escolhida. Se a solução entregue não passou pelo
    /// callback (ex.: resolvida na pré-resolução), ela é lida do solver e entra como a última.
    /// </summary>
    public EngineSolutionsSummary Summary(CpSolver solver, string status, double seconds)
    {
        var list = _kept.ToList();
        var total = Count;
        var final = (long)Math.Round(solver.ObjectiveValue);
        if (max > 0 && (list.Count == 0 || list[^1].Objetivo != final))
        {
            total = Math.Max(total, list.Count + 1);
            list.Add(Read(total, solver.WallTime(), solver.ObjectiveValue, solver.BestObjectiveBound, solver.Value));
            if (list.Count > max) list.RemoveAt(0);
        }
        if (list.Count > 0) list[^1] = list[^1] with { Escolhida = true };
        var components = EngineComponents.All.Where(model.ObjectiveComponents.ContainsKey)
            .Select(k => new EngineComponentInfo(k, EngineComponents.Rotulo(k), EngineComponents.Peso(k))).ToList();
        return new EngineSolutionsSummary(Math.Max(total, 1), list.Count, max, status, final, solver.BestObjectiveBound,
            Gap(solver.ObjectiveValue, solver.BestObjectiveBound), Math.Round(seconds, 3), model.ObjectiveOffset, components, list);
    }
}

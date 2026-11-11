using Escala5x2.BuildingBlocks.Contracts.Scheduling;

namespace Escala5x2.BuildingBlocks.Compliance;

/// <summary>
/// Posicionamento automático do intervalo de refeição (pós-processamento determinístico, depois do CP-SAT).
/// <para>
/// Por que fora do CP-SAT: o motor decide dia e turno de cada pessoa; com os turnos fixados, o início do almoço só
/// afeta a cobertura das faixas daquele dia/setor e as regras de intervalo da própria célula. O problema se separa
/// por dia/setor, é pequeno (dezenas de pessoas × dezenas de inícios possíveis) e uma busca gulosa seguida de
/// melhoria local (coordenada a coordenada) chega ao ótimo local sem aumentar o modelo do motor (que hoje tem uma
/// variável por pessoa × dia × turno; o almoço multiplicaria isso pelos inícios possíveis). Limitação conhecida: o
/// motor não "sabe" do almoço; se a demanda de uma faixa é igual ao quadro do turno, qualquer almoço abre um buraco,
/// que o avaliador passa a mostrar (antes ficava escondido).
/// </para>
/// <list type="number">
/// <item>Candidatos de início: de 15 em 15 minutos (no relógio), dentro da janela que cumpre TODAS as regras de
/// posição ativas (rígidas e preferenciais); se vazia, a janela das rígidas; se ainda vazia, qualquer posição dentro
/// da jornada (o avaliador aponta).</item>
/// <item>Critério (lexicográfico, sobre as faixas com demanda do dia/setor): 1º a MAIOR falta numa faixa (escalonar:
/// nunca concentrar os almoços a ponto de esvaziar o setor), 2º o número de faixas abaixo do exigido (a medida do
/// avaliador), 3º a distância do almoço ao meio da jornada, 4º o início mais cedo. Por que a maior falta vem antes:
/// quando a demanda é igual ao quadro do turno, juntar todos os almoços na mesma meia hora deixa só UMA faixa abaixo
/// (com zero pessoas), enquanto escalonar deixa várias faixas com 1 a menos; contar só faixas premiaria esvaziar o
/// caixa, que é o contrário de preservar a cobertura.</item>
/// <item>Gulosa: as pessoas com menos candidatos primeiro; depois passes de melhoria (cada pessoa é reposicionada com
/// as demais fixas) até nenhuma mudar — termina porque o critério acima (somado às distâncias e inícios) cai a cada troca.</item>
/// </list>
/// Células não móveis (almoço manual, ou fora do pedido) entram como estão: com início, descontam a faixa; sem início,
/// contam como presentes o turno inteiro. Duração do intervalo: a da célula ou a do turno (não é alterada aqui).
/// </summary>
public static class BreakPlanner
{
    public const int StepMinutes = 15;
    private const int MaxPasses = 50;

    private sealed class Mover(int index, CellTimes times, List<int> candidates)
    {
        public int Index { get; } = index;
        public CellTimes Times { get; } = times;
        public List<int> Candidates { get; } = candidates;
        public int Mid => (Times.Start + Times.EndAbs) / 2;
        public int? Position { get; set; }
    }

    /// <summary>
    /// Devolve as células na mesma ordem, com <see cref="AssignmentDto.BreakStart"/> definido nas móveis
    /// (<paramref name="movable"/>); célula móvel sem intervalo (0 min) fica sem início.
    /// </summary>
    public static List<AssignmentDto> Plan(PlanningContext ctx, IReadOnlyList<AssignmentDto> cells, Func<AssignmentDto, bool> movable)
    {
        var result = cells.ToList();
        var isMovable = result.Select(movable).ToArray();
        foreach (var group in Enumerable.Range(0, result.Count).GroupBy(i => (result[i].Date, result[i].SectorId)))
        {
            var day = ctx.DayIndex(group.Key.Date);
            if (day < 0 || day >= ctx.Days || !group.Any(i => isMovable[i])) continue;
            var sector = Array.FindIndex(ctx.Sectors, s => s.SectorId == group.Key.SectorId);
            var slots = ctx.Slots.Where(s => s.Day == day && s.Sector == sector).ToList();
            var policy = ctx.Interval(day);

            // Presença por faixa sem o intervalo das células móveis
            var present = new int[slots.Count];
            var movers = new List<Mover>();
            foreach (var i in group)
            {
                var times = ctx.Times(result[i]);
                for (var k = 0; k < slots.Count; k++)
                {
                    if (!PlanningContext.Covers(times.Start, times.EndAbs, slots[k])) continue;
                    if (isMovable[i] || !times.OnBreak(slots[k].SlotStart, slots[k].SlotStart + slots[k].SlotMinutes)) present[k]++;
                }
                if (!isMovable[i]) continue;
                if (times.BreakMinutes <= 0)
                {
                    result[i] = result[i] with { BreakStart = null };
                    continue;
                }
                movers.Add(new Mover(i, times, Candidates(policy, times)));
            }
            if (movers.Count == 0) continue;

            bool Overlaps(Mover m, int start, int k) =>
                PlanningContext.Covers(m.Times.Start, m.Times.EndAbs, slots[k]) &&
                start < slots[k].SlotStart + slots[k].SlotMinutes && start + m.Times.BreakMinutes > slots[k].SlotStart;

            void Apply(Mover m, int? start, int delta)
            {
                if (start is not { } s) return;
                for (var k = 0; k < slots.Count; k++)
                    if (Overlaps(m, s, k)) present[k] += delta;
            }

            // Cobertura do dia com a pessoa almoçando em `start` (as demais como estão):
            // 1º a maior falta numa faixa (não esvaziar o setor), 2º quantas faixas ficam abaixo, depois o meio da jornada
            (int MaxShort, int Below, int Distance, int Start) Score(Mover m, int start)
            {
                int maxShort = 0, below = 0;
                for (var k = 0; k < slots.Count; k++)
                {
                    var p = present[k] - (Overlaps(m, start, k) ? 1 : 0);
                    var shortfall = slots[k].Required - p;
                    if (shortfall <= 0) continue;
                    below++;
                    if (shortfall > maxShort) maxShort = shortfall;
                }
                return (maxShort, below, Math.Abs(start + m.Times.BreakMinutes / 2 - m.Mid), start);
            }

            int Best(Mover m) => m.Candidates.MinBy(c => Score(m, c));

            // Gulosa: os mais restritos primeiro
            foreach (var m in movers.OrderBy(m => m.Candidates.Count).ThenBy(m => m.Times.Start).ThenBy(m => result[m.Index].EmployeeId))
            {
                m.Position = Best(m);
                Apply(m, m.Position, -1);
            }

            // Melhoria local
            for (var pass = 0; pass < MaxPasses; pass++)
            {
                var changed = false;
                foreach (var m in movers)
                {
                    Apply(m, m.Position, +1);
                    var best = Best(m);
                    if (best != m.Position) changed = true;
                    m.Position = best;
                    Apply(m, best, -1);
                }
                if (!changed) break;
            }

            foreach (var m in movers)
                result[m.Index] = result[m.Index] with { BreakStart = TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(m.Position!.Value % PlanningContext.MinutesPerDay)) };
        }
        return result;
    }

    /// <summary>Inícios possíveis (múltiplos de 15 min no relógio) pela janela recomendada, legal ou, sem elas, a jornada inteira.</summary>
    private static List<int> Candidates(IntervalPolicy policy, CellTimes t)
    {
        var all = new List<int>();
        var first = (t.Start + StepMinutes - 1) / StepMinutes * StepMinutes;
        for (var s = first; s + t.BreakMinutes <= t.EndAbs; s += StepMinutes) all.Add(s);
        if (all.Count == 0) all.Add(t.Start);

        var recommended = all.Where(s => policy.PositionOk(t.Start, t.EndAbs, s, t.BreakMinutes, rigidOnly: false)).ToList();
        if (recommended.Count > 0) return recommended;
        var legal = all.Where(s => policy.PositionOk(t.Start, t.EndAbs, s, t.BreakMinutes, rigidOnly: true)).ToList();
        return legal.Count > 0 ? legal : all;
    }
}

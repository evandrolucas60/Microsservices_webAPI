using Escala5x2.BuildingBlocks.Contracts.Scheduling;

namespace Escala5x2.BuildingBlocks.Compliance;

/// <summary>Setor com exatamente 2 colaboradores disponíveis no mês: as duas pessoas das folgas fixas e intercaladas.</summary>
public sealed record FixedDaysOffPair(int Sector, EmployeeInfo A, EmployeeInfo B)
{
    public IEnumerable<EmployeeInfo> People => [A, B];
}

/// <summary>
/// FOLGAS FIXAS E INTERCALADAS em setor com 2 pessoas (<see cref="Contracts.Planning.OperationSnapshot.FolgasFixasSetorDuasPessoas"/>,
/// padrão ligado; loja sem operação: ligado). Decisões:
/// <list type="bullet">
/// <item><b>Quando</b>: o setor tem exatamente 2 colaboradores DISPONÍVEIS no período — com o setor na fotografia e ao menos
/// um dia do mês sem ausência e fora de outro setor. Quem está de férias/afastado o mês inteiro não conta (3 pessoas com uma
/// de férias o mês todo = 2: aplica). Setor com 1 pessoa (ou 3+): não se aplica, folgas variáveis como antes.</item>
/// <item><b>Fixas</b>: cada pessoa tem um padrão semanal de dias de folga (o número de folgas do regime: 2 no 5x2), o mesmo
/// toda semana. Nas semanas regulares (sem ausência, feriado, célula manual, loja fechada fora do padrão nem dia da escala
/// vizinha, de nenhum dos dois) quem não está no padrão trabalha (restrição dura no motor); nas irregulares o padrão vira
/// penalidade (as regras legais da semana e a cobertura mandam: na ausência de um, o outro cobre).</item>
/// <item><b>Intercaladas</b>: os padrões dos dois não têm dia em comum e, em todo dia com a loja aberta em que os dois
/// podem trabalhar, pelo menos um trabalha (as folgas nunca coincidem; o setor nunca fica vazio). Sem folgas_consecutivas_5x2
/// ativa, as folgas de cada um também não ficam em dias seguidos (por isso a prioridade de folga dupla não se aplica a
/// esses setores: não há folga dupla por desenho).</item>
/// <item><b>Domingo</b>: a regra de domingos (domingo_folga_janela_semanas, quinzenal) continua rígida. Como padrão fixo
/// sem domingo deixaria alguém sem domingo de folga, o domingo fica FORA da parte fixa: na semana em que a pessoa folga no
/// domingo (no ritmo que a regra exige, alternando entre os dois), ela trabalha num dos dias fixos — sempre o mesmo, o "dia
/// de troca" — e o resto do padrão não muda. Para o domingo de folga não virar folga dupla, com o rodízio de domingos a
/// segunda não entra no padrão e, se o sábado está no padrão, o sábado é o dia de troca. Domingo com a loja fechada toda
/// semana entra no padrão dos dois.</item>
/// <item><b>Loja fechada / feriado</b>: dia da semana sempre fechado entra no padrão (dos dois); feriado fechado ou sem
/// autorização não é folga (não é dia normal) e não mexe no padrão.</item>
/// <item><b>Continuidade</b>: as folgas e o trabalho registrados na escala vizinha (dias de borda e a semana antes/depois do
/// mês) puxam o padrão para o mesmo dos meses vizinhos (penalidade); sem escala vizinha, escolha livre.</item>
/// <item>Sem escala possível com o padrão (inviável), o motor gera com folgas variáveis e avisa.</item>
/// <item><b>Cobertura</b>: nesses setores a cobertura mínima é META, não restrição dura (<see cref="SoftCoverage"/>).</item>
/// </list>
/// </summary>
public sealed partial class PlanningContext
{
    private List<FixedDaysOffPair>? _fixedDaysOffPairs;

    /// <summary>A operação liga as folgas fixas em setor de 2 pessoas (sem operação: ligado).</summary>
    public bool FixedDaysOffEnabled => Operation?.FolgasFixasSetorDuasPessoas ?? true;

    /// <summary>Colaborador disponível no período para o setor: tem algum dia do mês sem ausência e fora de outro setor.</summary>
    public bool AvailableInPeriod(EmployeeInfo e) =>
        Enumerable.Range(FirstDay, PeriodDays).Any(d => IsAvailable(e, d) && !e.Busy.ContainsKey(d));

    /// <summary>Setores com exatamente 2 colaboradores disponíveis (vazio com a opção desligada).</summary>
    public IReadOnlyList<FixedDaysOffPair> FixedDaysOffPairs => _fixedDaysOffPairs ??= BuildFixedDaysOffPairs();

    private List<FixedDaysOffPair> BuildFixedDaysOffPairs()
    {
        var list = new List<FixedDaysOffPair>();
        if (!FixedDaysOffEnabled) return list;
        for (var s = 0; s < Sectors.Length; s++)
        {
            var sector = s;
            var people = Employees.Where(e => e.Sectors.Contains(sector) && AvailableInPeriod(e)).ToList();
            if (people.Count == 2) list.Add(new FixedDaysOffPair(s, people[0], people[1]));
        }
        return list;
    }

    /// <summary>A pessoa está num setor de folgas fixas e intercaladas.</summary>
    public bool HasFixedDaysOff(EmployeeInfo e) => FixedDaysOffPairs.Any(p => p.A == e || p.B == e);

    /// <summary>
    /// COBERTURA COMO META no setor de 2 pessoas (o mesmo critério das folgas fixas: a opção ligada e exatamente 2
    /// colaboradores disponíveis). Com 2 pessoas no 5x2 são no máximo 10 turnos na semana, e um expediente longo (ex.:
    /// 07:00–22:00, 2 turnos por dia = 14 na semana) não fecha: nos dias em que um folga o outro não cobre o dia sozinho.
    /// Por isso a cobertura mínima, os mínimos de abertura/fechamento e o reforço de pico desses setores são METAS: o motor
    /// maximiza o tempo coberto (prioridades em SolverOptions) e a escala sempre sai; o avaliador aponta a faixa descoberta
    /// como aviso (preferencial, severidade baixa), sem bloquear a aprovação. As regras legais (interjornada, jornada,
    /// dias seguidos, folgas do regime, domingos), ausências e loja fechada continuam rígidas.
    /// <para>Também é meta no setor com QUADRO INSUFICIENTE (<see cref="CoverageTargetSectors"/>, decidido pelo motor:
    /// PlanningContext.CoverageTarget).</para>
    /// </summary>
    public bool SoftCoverage(int sector) => FixedDaysOffPairs.Any(p => p.Sector == sector) || CoverageTargetSectors.Contains(sector);

    /// <summary>A cobertura do setor é meta por ser setor de 2 pessoas (folgas fixas e intercaladas).</summary>
    public bool TwoPersonCoverage(int sector) => FixedDaysOffPairs.Any(p => p.Sector == sector);

    /// <summary>
    /// O mínimo da faixa é restrição dura: só nos dias do período e fora dos setores com cobertura como meta (<see cref="SoftCoverage"/>).
    /// Faixa de dia de borda (semana parcial do mês vizinho sem escala vizinha) é só orientação: esta escala não grava
    /// esses dias, então a cobertura deles nunca torna o mês inviável (o motor só a usa como meta).
    /// </summary>
    public bool HardCoverage(CoverageSlot slot) => InPeriod(slot.Day) && !TargetCoverage(slot);

    /// <summary>Dia da semana na ordem segunda (0) .. domingo (6).</summary>
    public static int WeekdayIndex(DayOfWeek d) => ((int)d + 6) % 7;

    /// <summary>"seg", "ter", ... "dom".</summary>
    public static string WeekdayShort(DayOfWeek d) => d switch
    {
        DayOfWeek.Monday => "seg",
        DayOfWeek.Tuesday => "ter",
        DayOfWeek.Wednesday => "qua",
        DayOfWeek.Thursday => "qui",
        DayOfWeek.Friday => "sex",
        DayOfWeek.Saturday => "sáb",
        _ => "dom"
    };

    /// <summary>"ter/sex" (segunda primeiro); vazio = "—".</summary>
    public static string WeekdaysText(IEnumerable<DayOfWeek> days)
    {
        var list = days.Distinct().OrderBy(WeekdayIndex).Select(WeekdayShort).ToList();
        return list.Count == 0 ? "—" : string.Join("/", list);
    }

    /// <summary>
    /// Padrão de folgas observado nas células (para a tela e para conferir edições): por pessoa de setor de folgas fixas, os
    /// dias da semana com folga de descanso em MAIS da metade das ocorrências do período em que a pessoa podia trabalhar
    /// (sem ausência, feriado sem autorização, outro setor nem loja fechada), sem contar as semanas com domingo de folga
    /// (nelas um dia do padrão é trabalhado no lugar do domingo). O domingo em rodízio (folga 1 a cada N semanas) não
    /// entra: aparece em <see cref="FixedDaysOffView.DomingosDeFolga"/>.
    /// </summary>
    public List<FixedDaysOffView> FixedDaysOffViews(IEnumerable<AssignmentDto> work)
    {
        var worked = work.Select(a => (a.EmployeeId, a.Date)).ToHashSet();
        var result = new List<FixedDaysOffView>();
        foreach (var pair in FixedDaysOffPairs)
        foreach (var e in pair.People)
        {
            var id = e.Snapshot.EmployeeId;
            bool Candidate(int d) => !e.Busy.ContainsKey(d) && !NonWorkable(e, d) && !IsClosed(d);
            bool Rest(int d) => Candidate(d) && !worked.Contains((id, Dates[d]));
            // Semanas com domingo de folga: a troca de um dia do padrão não conta (senão o rodízio esconde o padrão)
            var sundayWeeks = Enumerable.Range(0, Weeks).Where(w => InPeriod(w * 7 + 6) && Rest(w * 7 + 6)).ToHashSet();
            var candidates = new int[7];
            var rests = new int[7];
            var all = new (int Candidates, int Rests)[7];
            for (var d = FirstDay; d <= LastDay; d++)
            {
                if (!Candidate(d)) continue;
                var dow = WeekdayIndex(Dates[d].DayOfWeek);
                var rest = Rest(d) ? 1 : 0;
                all[dow] = (all[dow].Candidates + 1, all[dow].Rests + rest);
                if (dow != 6 && sundayWeeks.Contains(d / 7)) continue;
                candidates[dow]++;
                rests[dow] += rest;
            }
            for (var i = 0; i < 7; i++)
                if (candidates[i] == 0) (candidates[i], rests[i]) = all[i];
            var pattern = Enumerable.Range(0, 7).Where(i => candidates[i] > 0 && rests[i] * 2 > candidates[i])
                .Select(i => (DayOfWeek)((i + 1) % 7)).ToList();
            var sundaysOff = all[6].Rests;
            result.Add(new FixedDaysOffView(id, e.Snapshot.Name, Sectors[pair.Sector].Name, pattern,
                pattern.Contains(DayOfWeek.Sunday) ? 0 : sundaysOff));
        }
        return result;
    }

    /// <summary>"Ana: ter/sex, Bruno: qua/sáb".</summary>
    public static string FixedDaysOffText(IEnumerable<FixedDaysOffView> views) =>
        string.Join(", ", views.Select(v => $"{v.Nome}: {WeekdaysText(v.Dias)}"));
}

/// <summary>
/// Folgas fixas de uma pessoa (setor de 2 pessoas): <paramref name="Dias"/> = dias da semana do padrão;
/// <paramref name="DomingosDeFolga"/> = domingos de folga no período fora do padrão (rodízio da regra de domingos).
/// </summary>
public sealed record FixedDaysOffView(Guid EmployeeId, string Nome, string Setor, IReadOnlyList<DayOfWeek> Dias, int DomingosDeFolga)
{
    public string Texto => PlanningContext.WeekdaysText(Dias);
}

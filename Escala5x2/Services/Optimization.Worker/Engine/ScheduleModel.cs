using Escala5x2.BuildingBlocks.Compliance;
using Escala5x2.BuildingBlocks.Contracts.Compliance;
using Escala5x2.BuildingBlocks.Contracts.Planning;
using Escala5x2.BuildingBlocks.Contracts.Scheduling;
using Google.OrTools.Sat;

namespace Optimization.Worker.Engine;

/// <summary>
/// Para que o modelo é montado. Não existe mais "modo flexível" na geração: regra rígida e mínimo de cobertura são
/// SEMPRE restrições duras; se não houver escala, o resultado é Inviável com diagnóstico.
/// </summary>
public enum SolveStage
{
    /// <summary>
    /// Geração: regras rígidas, mínimos de cobertura (fora do intervalo), perfil, disponibilidade, ausências, células
    /// manuais e continuidade são restrições duras; regras preferenciais, reforço de pico, excedente, equilíbrio,
    /// mudança de horário, preferências e custos entram na função objetivo (pesos em <see cref="SolverOptions"/>).
    /// O mínimo de cobertura do setor de 2 pessoas, do setor com quadro insuficiente e dos dias de borda é meta (PlanningContext.HardCoverage).
    /// </summary>
    Generation,
    /// <summary>
    /// Diagnóstico de inviabilidade: igual à geração, mas a falta de gente no mínimo de cobertura é medida (variável de
    /// falta) e minimizada; preferenciais fora. Diz quais faixas/dias não fecham com todas as outras regras.
    /// </summary>
    CoverageRelaxed,
    /// <summary>
    /// Sugestões de correção de uma escala EXISTENTE (mudança mínima): toda regra vira penalidade para propor alterações
    /// que reduzam as violações. Não gera nem entrega escala: o operador aplica (ou não) e o avaliador mostra o resultado.
    /// </summary>
    Correction
}

/// <summary>Variável de início do intervalo: início absoluto, duração e a variável.</summary>
public sealed record BreakVar(int Start, int Minutes, BoolVar Var);

/// <summary>
/// Modelo CP-SAT da escala, com TODOS os parâmetros legais vindos das regras de conformidade da fotografia
/// (PlanningContext.Rules), resolvidos por dia. Regra bloqueada não entra no modelo. Aplicação Rígida = restrição;
/// Preferencial = penalidade (WeightPreferentialViolation).
/// <list type="bullet">
/// <item>R1 um turno/setor por dia; R8 férias/afastamentos/feriados; perfil de jornada (só turnos do tipo do perfil; com o
/// rodízio de abertura/fechamento ligado, turno de outro tipo com penalidade — ver AddRotation);
/// disponibilidade semanal; células manuais fixas.</item>
/// <item>Intervalo NO MODELO: variáveis de início do intervalo por pessoa × dia (posições de 15 em 15 min que cumprem
/// as regras de posição rígidas de pelo menos um turno escolhível; ligadas ao turno escolhido). A cobertura conta só
/// quem está FORA do intervalo (conservador: um intervalo que toca uma faixa desconta a pessoa da faixa inteira).</item>
/// <item>R2 dias seguidos, R3 folgas (número exato do regime + piso do DSR + folgas_consecutivas_5x2), R3b no máximo N
/// folgas seguidas (max_folgas_seguidas, inclusive na virada de semana e na emenda), R4 carga semanal e
/// hora extra, R5 interjornada, R5b folga isolada, R6 domingos — com o histórico (semana anterior) e a escala
/// seguinte publicada/aprovada.</item>
/// <item>R7 cobertura: mínimo RÍGIDO por faixa (operação: cobertura mínima, abertura, fechamento); reforço de pico
/// e excedente na função objetivo. Exceções (<see cref="PlanningContext.HardCoverage"/>): no setor de 2 pessoas, no setor com quadro insuficiente e nos dias
/// de borda o mínimo é META (falta penalizada, TargetWeight) — a escala sempre sai.</item>
/// <item>Setor de 2 pessoas (só na geração): folgas fixas e intercaladas (AddFixedDaysOff).</item>
/// </list>
/// Com <c>assumptions</c>, cada grupo de restrições duras (regra, cobertura de um dia, células manuais de uma pessoa,
/// continuidade...) fica condicionado a um literal de hipótese: o CP-SAT devolve um conjunto de grupos que, juntos,
/// tornam o problema impossível (diagnóstico de inviabilidade).
/// </summary>
public sealed class ScheduleModel
{
    private readonly PlanningContext _ctx;
    private readonly SolverOptions _o;
    private readonly SolveStage _stage;
    private readonly bool _assume;
    private readonly IReadOnlySet<string> _refine;
    /// <summary>
    /// Pessoa e dias (índices) da restrição em montagem, para o refinamento do diagnóstico (<c>refine</c>); Item = texto
    /// próprio do grupo (null = genérico: regra, pessoa e dias).
    /// </summary>
    private (EmployeeInfo Emp, int First, int Last, Func<DiagnosisItem>? Item)? _scope;
    private readonly bool _rotation;
    private readonly bool _fixedDaysOff;
    private readonly HashSet<string> _warnings = [];
    private readonly LinearExprBuilder _obj = LinearExpr.NewBuilder();
    private int _softCount;
    private readonly Dictionary<string, LinearExprBuilder> _components = new();
    private readonly Dictionary<string, List<LinearExpr>> _indicators = new();
    private long _offset;

    /// <summary>
    /// Detalhamento da função objetivo: penalidade de cada componente (<see cref="EngineComponents"/>) como expressão,
    /// avaliada a cada solução no registro das soluções. Mesmos termos do objetivo, separados por componente; as regras
    /// preferenciais aparecem como peso × descumprimentos (constante em <see cref="ObjectiveOffset"/>).
    /// </summary>
    public IReadOnlyDictionary<string, LinearExprBuilder> ObjectiveComponents => _components;
    /// <summary>objetivo do CP-SAT = Σ componentes − ObjectiveOffset.</summary>
    public long ObjectiveOffset => _offset;
    /// <summary>Contagens por pessoa (aberturas, fechamentos, domingos) para os indicadores mín–máx de cada solução.</summary>
    public IReadOnlyDictionary<string, List<LinearExpr>> Indicators => _indicators;

    public CpModel Model { get; } = new();
    public Dictionary<(int E, int D, int T, int S), BoolVar> X { get; } = new();
    /// <summary>Inícios possíveis do intervalo por pessoa × dia × setor (só nos dias com faixas de cobertura no setor).</summary>
    public Dictionary<(int E, int D, int S), List<BreakVar>> Breaks { get; } = new();
    public BoolVar[,] Y { get; }
    /// <summary>Geração: faltas no reforço de pico (preferencial). Diagnóstico: faltas no mínimo de cobertura.</summary>
    public List<(CoverageSlot Slot, IntVar Deficit)> Deficits { get; } = [];
    /// <summary>
    /// Geração: faltas no mínimo das faixas em que a cobertura é META (setor de 2 pessoas, quadro insuficiente e dias de borda,
    /// <see cref="PlanningContext.HardCoverage"/>): o tempo descoberto que o motor minimiza e o aviso lista.
    /// </summary>
    public List<(CoverageSlot Slot, IntVar Deficit)> TargetDeficits { get; } = [];
    public List<IntVar> Overtime { get; } = [];
    public IReadOnlyCollection<string> Warnings => _warnings;
    public int SoftConstraints => _softCount;
    /// <summary>Grupos de restrições duras com literal de hipótese (só com assumptions).</summary>
    public Dictionary<string, (BoolVar Literal, DiagnosisItem Item)> Groups { get; } = new();
    /// <summary>
    /// Folgas fixas (setor de 2 pessoas) por colaborador (índice): P[i] = folga fixa no dia da semana i (0 = segunda ..
    /// 6 = domingo) e Q[i] = dia de troca pelo domingo de folga. Vazio quando não se aplica.
    /// </summary>
    public Dictionary<int, (BoolVar[] P, BoolVar[] Q)> FixedPatterns { get; } = new();
    /// <summary>As folgas fixas e intercaladas estão no modelo.</summary>
    public bool FixedDaysOffApplied => FixedPatterns.Count > 0;

    private readonly IntVar?[,] _start;
    private readonly IntVar?[,] _end;
    private readonly Dictionary<(int E, int D, int T), List<BoolVar>> _byShift = new();
    private readonly Dictionary<(int D, int S), List<(int E, int T, BoolVar X)>> _byDaySector = new();
    /// <summary>Por pessoa × dia × setor: turnos com intervalo e as posições legais de cada um.</summary>
    private readonly Dictionary<(int E, int D, int S), List<(ShiftInfo T, BoolVar X, HashSet<int> Legal)>> _breakShifts = new();
    /// <summary>Rodízio de abertura/fechamento: variáveis de turno fora do perfil (penalizadas; não inclui células fixas).</summary>
    private readonly List<(int E, int D, int T, int S, BoolVar X)> _outOfProfile = [];

    /// <param name="baseline">Sugestões: valor atual das células mutáveis (turno ou null = sem trabalho); cada mudança é penalizada.</param>
    /// <param name="assumptions">Diagnóstico: grupos de restrições duras com literais de hipótese.</param>
    /// <param name="feasibilityOnly">Sem função objetivo (diagnóstico por hipóteses).</param>
    /// <param name="rotation">
    /// Rodízio de abertura/fechamento no modelo (null = o da operação, <see cref="PlanningContext.Rotation"/>; false = perfil
    /// como restrição dura, usado na primeira tentativa da geração).
    /// </param>
    /// <param name="fixedDaysOff">
    /// Folgas fixas e intercaladas nos setores de 2 pessoas (<see cref="PlanningContext.FixedDaysOffPairs"/>; só na geração).
    /// null/true = conforme a operação e <see cref="SolverOptions.FixedDaysOffTwoPersonSectors"/>; false = folgas variáveis
    /// (nova tentativa quando o padrão deixa a escala inviável, e o diagnóstico de inviabilidade).
    /// </param>
    /// <param name="refine">
    /// Diagnóstico: regras (chaves) cujas restrições duras ficam em grupos POR PESSOA E DIAS (ex.: "regra:max_folgas_seguidas:3:12:14")
    /// em vez de um grupo só da regra — para apontar quem e quando quando a regra aparece sozinha no núcleo.
    /// </param>
    public ScheduleModel(PlanningContext ctx, SolveStage stage, SolverOptions options,
        IReadOnlyDictionary<(int E, int D), int?>? baseline = null, bool assumptions = false, bool feasibilityOnly = false, bool? rotation = null,
        bool? fixedDaysOff = null, IReadOnlySet<string>? refine = null)
    {
        _ctx = ctx;
        _o = options;
        _stage = stage;
        _assume = assumptions;
        _refine = refine ?? new HashSet<string>();
        _rotation = rotation ?? ctx.Rotation;
        _fixedDaysOff = fixedDaysOff != false && stage == SolveStage.Generation && options.FixedDaysOffTwoPersonSectors
                        && ctx.FixedDaysOffPairs.Count > 0;
        Y = new BoolVar[ctx.Employees.Length, ctx.Days];
        _start = new IntVar?[ctx.Employees.Length, ctx.Days];
        _end = new IntVar?[ctx.Employees.Length, ctx.Days];

        BuildAssignmentVariables();      // R1, R8, perfil, disponibilidade, células fixas, limites por turno, feriado
        BuildBreakVariables();           // intervalo dentro do modelo
        AddConsecutiveDays();            // R2  dias_consecutivos_max_6x1 / _5x2 (com histórico e escala seguinte)
        AddWeeklyRest();                 // R3  folgas do regime + piso folgas_minimas_semana + folgas_consecutivas_5x2
        AddMaxDaysOff();                 // R3b max_folgas_seguidas (com histórico e escala seguinte)
        AddWeeklyHoursAndOvertime();     // R4  carga_horaria_semanal + hora_extra_max_diaria_minutos
        AddInterShiftRest();             // R5  interjornada_minima_minutos
        AddIsolatedDayOffRest();         // R5b folga_isolada_horas_livres
        AddSundayRules();                // R6  domingo_folga_janela_semanas / domingo_quinzenal_janela_semanas
        AddCoverage();                   // R7  mínimo rígido + reforço de pico + excedente
        if (_fixedDaysOff) AddFixedDaysOff(); // setor de 2 pessoas: folgas fixas e intercaladas

        if (feasibilityOnly) return;
        if (stage == SolveStage.CoverageRelaxed)
        {
            Model.Minimize(Sum(Deficits.Select(d => d.Deficit)));
            return;
        }
        // Dicas da cobertura como meta antes das do rodízio (a primeira dica de cada variável vale)
        if (stage == SolveStage.Generation) AddCoverageTargetHints();
        AddObjectiveTerms();
        if (baseline is not null) AddChangePenalty(baseline);
        Model.Minimize(_obj);
    }

    // ---------------- helpers ----------------

    /// <summary>Soma sem ambiguidade de sobrecarga (BoolVar é IntVar e ILiteral ao mesmo tempo).</summary>
    public static LinearExpr Sum(IEnumerable<IntVar> vars)
    {
        var b = LinearExpr.NewBuilder();
        foreach (var v in vars) b.Add((LinearExpr)v);
        return b;
    }

    private bool Hard(bool rigid) => rigid && _stage != SolveStage.Correction;

    /// <summary>Termo da função objetivo, registrado também no componente (detalhamento por solução).</summary>
    private void Term(string component, LinearExpr expr, long coefficient)
    {
        _obj.AddTerm(expr, coefficient);
        Component(component).AddTerm(expr, coefficient);
    }

    /// <summary>
    /// Penalidade "peso × (1 − literal)": no objetivo só entra −peso × literal (a constante não muda a otimização nem o
    /// valor gravado do objetivo); o componente leva a constante, para mostrar o custo real do descumprimento.
    /// </summary>
    private void Penalty(string component, LinearExpr ok, long weight)
    {
        _obj.AddTerm(ok, -weight);
        var c = Component(component);
        c.Add(weight);
        c.AddTerm(ok, -weight);
        _offset += weight;
    }

    private LinearExprBuilder Component(string key)
    {
        if (!_components.TryGetValue(key, out var b)) _components[key] = b = LinearExpr.NewBuilder();
        return b;
    }

    private string Desc(string key) => _ctx.Rules.Representative(key)?.Descricao ?? key;

    private DiagnosisItem RuleItem(string key) => new("regra", $"{Desc(key)} ({key})", Chave: key);

    /// <summary>Item do grupo refinado (pessoa e dias): "Ana — Máximo de dias seguidos (chave) de 03/11 a 09/11".</summary>
    private DiagnosisItem ScopeItem(string key, EmployeeInfo emp, int first, int last)
    {
        var when = first == last ? $"em {_ctx.Start.AddDays(first):dd/MM}" : $"de {_ctx.Start.AddDays(first):dd/MM} a {_ctx.Start.AddDays(last):dd/MM}";
        var dates = Enumerable.Range(first, last - first + 1).Where(_ctx.InPeriod).Select(d => _ctx.Dates[d]).ToList();
        return new DiagnosisItem("regra", $"{emp.Snapshot.Name} — {Desc(key)} ({key}) {when}", dates, emp.Snapshot.EmployeeId, key);
    }

    /// <summary>Executa <paramref name="add"/> com o escopo (pessoa e dias) do refinamento do diagnóstico.</summary>
    private void Scoped(EmployeeInfo emp, int first, int last, Action add, Func<DiagnosisItem>? item = null)
    {
        _scope = (emp, first, last, item);
        try { add(); }
        finally { _scope = null; }
    }

    /// <summary>
    /// Grupo de diagnóstico da continuidade quando a restrição usa dias de fora do horizonte (<paramref name="outside"/>:
    /// dias com trabalho/folga já registrados na escala anterior ou seguinte). Um grupo por emenda e regra: o diagnóstico
    /// diz qual emenda (anterior: início da escala; seguinte: fim) e qual regra não fecha com o que já está escalado lá.
    /// (null, null) = restrição só do horizonte.
    /// </summary>
    private (string? Group, Func<DiagnosisItem>? Item) Continuity(string key, IEnumerable<int> outside)
    {
        // Fora do período: antes/depois do horizonte e os dias de borda (semana parcial do mês vizinho)
        var days = outside.Where(k => !_ctx.InPeriod(k)).ToList();
        if (days.Count == 0) return (null, null);
        var before = days.Any(k => k < _ctx.FirstDay);
        var side = before ? "anterior" : "seguinte";
        var seam = before ? _ctx.PeriodStart : _ctx.PeriodEnd;
        return ($"continuidade:{side}:{key}", () => new DiagnosisItem("continuidade",
            $"Continuidade na emenda com a escala {side} ({(before ? "início" : "fim")} da escala em {seam:dd/MM}): {Desc(key)} ({key}) " +
            $"não fecha com o que já está escalado na escala {side} (a principal do setor ou, sem ela, a de melhor status)",
            [seam], Chave: key));
    }

    /// <summary>Literal de hipótese do grupo (só no diagnóstico por hipóteses).</summary>
    private void Guard(Constraint c, string group, Func<DiagnosisItem> item)
    {
        if (!_assume) return;
        if (!Groups.TryGetValue(group, out var g)) Groups[group] = g = (Model.NewBoolVar($"g_{Groups.Count}"), item());
        c.OnlyEnforceIf(g.Literal);
    }

    /// <summary>
    /// expr ≤ rhs (opcionalmente só quando <paramref name="when"/> são todos verdadeiros), conforme a aplicação da regra:
    /// rígida = restrição dura (grupo de diagnóstico <paramref name="group"/>, padrão "regra:chave"); senão penalidade por
    /// descumprimento. No diagnóstico de cobertura as preferenciais não entram.
    /// </summary>
    private void Le(LinearExpr expr, long rhs, bool rigid, string key, ILiteral[]? when = null, string? group = null, Func<DiagnosisItem>? item = null)
    {
        if (Hard(rigid))
        {
            var c = Model.Add(expr <= rhs);
            if (when is { Length: > 0 }) c.OnlyEnforceIf(when);
            // Refinamento do diagnóstico: um grupo por pessoa e dias (a continuidade continua por emenda)
            if (_assume && group is null && _scope is { } sc && _refine.Contains(key))
            {
                group = $"regra:{key}:{sc.Emp.Index}:{sc.First}:{sc.Last}";
                item = sc.Item ?? (() => ScopeItem(key, sc.Emp, sc.First, sc.Last));
            }
            Guard(c, group ?? $"regra:{key}", item ?? (() => RuleItem(key)));
            return;
        }
        if (_stage == SolveStage.CoverageRelaxed) return;
        var ok = Model.NewBoolVar($"ok_{key}_{_softCount++}");
        // Enforcement literals se acumulam (todos precisam ser verdadeiros)
        var soft = Model.Add(expr <= rhs);
        soft.OnlyEnforceIf(ok);
        if (when is { Length: > 0 }) soft.OnlyEnforceIf(when);
        // penalidade = peso × (1 − ok); a constante não muda a otimização
        Penalty(rigid ? EngineComponents.ViolacaoRigida : EngineComponents.ViolacaoPreferencial, (LinearExpr)ok,
            rigid ? _o.WeightRigidViolation : _o.WeightPreferentialViolation);
    }

    private LinearExpr? ShiftExpr(int e, int d, int t) =>
        _byShift.TryGetValue((e, d, t), out var list) && list.Count > 0 ? Sum(list) : null;

    /// <summary>
    /// Situação de um dia da pessoa: variável (dia do horizonte com turnos possíveis), constante trabalhada (histórico,
    /// escala seguinte, outro setor) ou não trabalhada.
    /// </summary>
    private readonly record struct Day(BoolVar? Y, IntVar? Start, IntVar? End, bool ConstWorked, int CStart, int CEnd, bool Outside)
    {
        public bool IsVar => Y is not null;
        public bool Off => !IsVar && !ConstWorked;
    }

    private Day DayOf(EmployeeInfo emp, int d)
    {
        if (d < 0)
            return emp.History.TryGetValue(_ctx.Start.AddDays(d), out var h) ? new(null, null, null, true, h.Start, h.EndAbs, true) : default;
        if (d >= _ctx.Days)
            return _ctx.FollowingAt(emp, d) is { } f ? new(null, null, null, true, f.Start, f.EndAbs, true) : default;
        // Ocupado em outro setor ou, no dia de borda, trabalho registrado na escala vizinha (continuidade)
        if (emp.Busy.TryGetValue(d, out var b)) return new(null, null, null, true, b.Start, b.EndAbs, !_ctx.InPeriod(d));
        return _start[emp.Index, d] is { } s ? new(Y[emp.Index, d], s, _end[emp.Index, d], false, 0, 0, false) : default;
    }

    /// <summary>Trabalhou no dia: variável do setor, ou constante 1 se já está em outro setor / no histórico / na escala seguinte.</summary>
    private (LinearExpr? Var, int Const) Worked(EmployeeInfo emp, int d)
    {
        var day = DayOf(emp, d);
        if (day.IsVar) return ((LinearExpr)day.Y!, 0);
        return (null, day.ConstWorked ? 1 : 0);
    }

    /// <summary>Soma de termos (variável ou constante); devolve null se não houver variável.</summary>
    private static (LinearExpr? Expr, int Const) Combine(IEnumerable<(LinearExpr? Var, int Const)> terms)
    {
        var b = LinearExpr.NewBuilder();
        var any = false;
        var c = 0;
        foreach (var (v, k) in terms)
        {
            if (v is not null) { b.Add(v); any = true; }
            c += k;
        }
        return (any ? b : null, c);
    }

    private bool HasFollowing(EmployeeInfo emp) => emp.Following.Count > 0;

    /// <summary>Último dia (índice) considerado depois do horizonte para a pessoa (escala seguinte conhecida).</summary>
    private int LastDay(EmployeeInfo emp) => HasFollowing(emp) ? _ctx.Days - 1 + _ctx.ContinuityDays : _ctx.Days - 1;

    // ---------------- R1 / R8 / perfil / disponibilidade / células fixas / regras por turno ----------------

    private void BuildAssignmentVariables()
    {
        var slotDays = _ctx.Slots.Select(s => (s.Day, s.Sector)).ToHashSet();
        foreach (var emp in _ctx.Employees)
        {
            var manual = $"manual:{emp.Index}";
            DiagnosisItem ManualItem() => new("celulas_manuais",
                $"Células inseridas manualmente para {emp.Snapshot.Name} (mantidas na geração \"só os dias não inseridos\")", EmployeeId: emp.Snapshot.EmployeeId);

            for (var d = 0; d < _ctx.Days; d++)
            {
                // Regras por turno e de feriado: refinamento do diagnóstico por pessoa e dia
                _scope = (emp, d, d, null);
                var y = Model.NewBoolVar($"y_{emp.Index}_{d}");
                Y[emp.Index, d] = y;
                var dayVars = new List<IntVar>();
                var startTerms = LinearExpr.NewBuilder();
                var endTerms = LinearExpr.NewBuilder();

                var hasFixed = _ctx.Fixed.TryGetValue((emp.Index, d), out var fixedShift);
                // Diagnóstico por hipóteses: a folga manual vira restrição do grupo das células manuais (y = 0) sobre um dia
                // com os turnos possíveis — senão ela seria constante e o núcleo culparia só a regra (ex.: max_folgas_seguidas)
                var manualRest = hasFixed && fixedShift is null && _assume;
                // Dia de borda com folga registrada na escala vizinha: sem turno possível (folga constante)
                var available = _ctx.IsAvailable(emp, d) && !emp.Busy.ContainsKey(d) && !emp.EdgeRest.Contains(d);
                var holiday = _ctx.HolidayRule(d);
                var holidayHard = holiday is { } h && Hard(h.Rigid);
                var day = d;

                foreach (var t in _ctx.Shifts)
                {
                    var forced = hasFixed && fixedShift == t.Index;
                    var issues = _ctx.ShiftIssues(emp, d, t).ToList();
                    var options = t.Snapshot.BreakMinutes > 0 ? _ctx.BreakOptions(d, t, t.Snapshot.BreakMinutes) : [];
                    if (!forced)
                    {
                        if ((hasFixed && !manualRest) || !available || holidayHard || _ctx.IsClosed(d)) continue; // loja fechada no dia: ninguém escalado
                        if (!_ctx.OpenAt(d, t)) continue;                    // turno começa fora do expediente do dia
                        if (issues.Any(i => Hard(i.Rigid))) continue;       // turno proibido por regra rígida
                        if (!_ctx.ProfileAllows(emp, t) && !(_rotation && _ctx.RotationAllows(emp, t))) continue; // perfil de jornada (ou rodízio, com penalidade)
                        if (!_ctx.AvailableAt(emp, d, t)) continue;          // disponibilidade semanal
                        if (t.Snapshot.BreakMinutes > 0 && options.Count == 0 && _stage != SolveStage.Correction) continue; // sem posição legal de intervalo
                    }

                    // Célula fixa de alguém sem o setor na fotografia: usa o primeiro setor (o avaliador aponta)
                    var sectors = emp.Sectors.Length > 0 ? emp.Sectors : forced && _ctx.Sectors.Length > 0 ? [0] : [];
                    var forShift = new List<IntVar>();
                    foreach (var s in sectors)
                    {
                        var x = Model.NewBoolVar($"x_{emp.Index}_{d}_{t.Index}_{s}");
                        X[(emp.Index, d, t.Index, s)] = x;
                        forShift.Add(x);
                        dayVars.Add(x);
                        startTerms.AddTerm((LinearExpr)x, t.StartMin);
                        endTerms.AddTerm((LinearExpr)x, t.EndMin);
                        if (!_byShift.TryGetValue((emp.Index, d, t.Index), out var l1)) _byShift[(emp.Index, d, t.Index)] = l1 = [];
                        l1.Add(x);
                        if (!_byDaySector.TryGetValue((d, s), out var l2)) _byDaySector[(d, s)] = l2 = [];
                        l2.Add((emp.Index, t.Index, x));
                        if (!forced && !_ctx.ProfileAllows(emp, t)) _outOfProfile.Add((emp.Index, d, t.Index, s, x));

                        // Intervalo: só nos dias com faixas de cobertura no setor (fora delas o BreakPlanner posiciona depois)
                        var fixedBreak = forced && _ctx.FixedBreaks.TryGetValue((emp.Index, d), out var fb) ? fb : (int?)null;
                        if (t.Snapshot.BreakMinutes > 0 && (slotDays.Contains((d, s)) || fixedBreak is not null))
                        {
                            var legal = fixedBreak is { } f ? [f] : options.Select(o => o.Start).ToHashSet();
                            if (legal.Count == 0)
                            {
                                // Correção (sugestão) ou célula fixa sem posição legal: qualquer posição dentro da jornada
                                for (var p = t.StartMin; p + t.Snapshot.BreakMinutes <= t.EndMin; p += BreakPlanner.StepMinutes) legal.Add(p);
                            }
                            if (!_breakShifts.TryGetValue((emp.Index, d, s), out var bl)) _breakShifts[(emp.Index, d, s)] = bl = [];
                            bl.Add((t, x, legal));
                        }
                    }
                    if (forShift.Count == 0) continue;
                    if (forced)
                    {
                        var c = Model.Add(Sum(forShift) == 1);
                        Guard(c, manual, ManualItem);
                        if (!_ctx.AvailableAt(emp, d, t))
                        {
                            // Célula manual fora da disponibilidade semanal: conflito explícito (disponibilidade é rígida)
                            var a = Model.Add(Sum(forShift) == 0);
                            Guard(a, $"disponibilidade:{emp.Index}", () => new DiagnosisItem("disponibilidade",
                                $"Disponibilidade semanal de {emp.Snapshot.Name} ({_ctx.AvailabilityText(emp, day)} em {_ctx.Dates[day]:dd/MM})",
                                [_ctx.Dates[day]], emp.Snapshot.EmployeeId, RuleKeys.Disponibilidade));
                        }
                        if (t.Snapshot.BreakMinutes > 0 && options.Count == 0 && !_ctx.FixedBreaks.ContainsKey((emp.Index, d)) && _stage != SolveStage.Correction)
                        {
                            var nb = Model.Add(Sum(forShift) == 0);
                            Guard(nb, $"regra:{RuleKeys.JornadaContinuaMaxima}", () => RuleItem(RuleKeys.JornadaContinuaMaxima));
                        }
                        if (_ctx.FixedBreaks.TryGetValue((emp.Index, d), out var manualBreak) && t.Snapshot.BreakMinutes > 0
                            && options.All(o => o.Start != manualBreak) && _stage != SolveStage.Correction)
                        {
                            // Intervalo ajustado à mão fora das regras de posição rígidas: a célula manual não pode ser mantida
                            var ib = Model.Add(Sum(forShift) == 0);
                            Guard(ib, $"intervalo_manual:{emp.Index}", () => new DiagnosisItem("celulas_manuais",
                                $"Intervalo ajustado à mão de {emp.Snapshot.Name} em {_ctx.Dates[day]:dd/MM} fora das regras de posição do intervalo",
                                [_ctx.Dates[day]], emp.Snapshot.EmployeeId));
                        }
                    }
                    // Limite diário/hora extra, limite de domingo e duração do intervalo: usar o turno descumpre a regra
                    foreach (var issue in issues) Le(Sum(forShift), 0, issue.Rigid, issue.Key);
                }

                // R1: um turno e um setor por dia; sem variáveis, y = 0 (férias, afastamento, feriado, outro setor).
                Model.Add(Sum(dayVars) == (LinearExpr)y);
                if (dayVars.Count > 0)
                {
                    var start = Model.NewIntVar(0, PlanningContext.MinutesPerDay, $"st_{emp.Index}_{d}");
                    var end = Model.NewIntVar(0, 2 * PlanningContext.MinutesPerDay, $"en_{emp.Index}_{d}");
                    Model.Add((LinearExpr)start == startTerms);
                    Model.Add((LinearExpr)end == endTerms);
                    _start[emp.Index, d] = start;
                    _end[emp.Index, d] = end;
                }
                if (hasFixed && fixedShift is null)
                {
                    var c = Model.Add((LinearExpr)y == 0); // folga manual
                    Guard(c, manual, ManualItem);
                }
                if (holiday is { } hr && dayVars.Count > 0) Le((LinearExpr)y, 0, hr.Rigid, hr.Key);
            }
        }
        _scope = null;
    }

    // ---------------- Intervalo dentro do modelo ----------------

    /// <summary>
    /// Por pessoa × dia × setor e por duração de intervalo: uma variável por posição possível (união das posições legais
    /// dos turnos escolhíveis). Σ posições = Σ turnos com essa duração; cada posição só pode ser usada se o turno escolhido
    /// a admite (posição ≤ Σ turnos em que ela é legal). Posição fora das regras preferenciais de posição é penalizada.
    /// </summary>
    private void BuildBreakVariables()
    {
        foreach (var ((e, d, s), shifts) in _breakShifts)
        {
            var list = new List<BreakVar>();
            foreach (var group in shifts.GroupBy(x => x.T.Snapshot.BreakMinutes))
            {
                var minutes = group.Key;
                var positions = group.SelectMany(x => x.Legal).Distinct().Order().ToList();
                var zs = new List<IntVar>();
                foreach (var p in positions)
                {
                    var z = Model.NewBoolVar($"b_{e}_{d}_{s}_{p}");
                    zs.Add(z);
                    list.Add(new BreakVar(p, minutes, z));
                    var legalFor = group.Where(x => x.Legal.Contains(p)).ToList();
                    Model.Add((LinearExpr)z <= Sum(legalFor.Select(x => (IntVar)x.X)));
                    if (_stage == SolveStage.CoverageRelaxed) continue;
                    // Regras de posição preferenciais (início mínimo após a entrada, fim mínimo antes da saída)
                    var recommended = legalFor.Where(x => _ctx.BreakOptions(d, x.T, minutes).Any(o => o.Start == p && o.Recommended)).ToList();
                    if (recommended.Count == legalFor.Count) continue;
                    if (recommended.Count == 0)
                    {
                        Term(EngineComponents.ViolacaoPreferencial, (LinearExpr)z, _o.WeightPreferentialViolation);
                        continue;
                    }
                    var rec = Model.NewBoolVar($"br_{e}_{d}_{s}_{p}");
                    Model.Add((LinearExpr)rec <= (LinearExpr)z);
                    Model.Add((LinearExpr)rec <= Sum(recommended.Select(x => (IntVar)x.X)));
                    Term(EngineComponents.ViolacaoPreferencial, (LinearExpr)z, _o.WeightPreferentialViolation);
                    Term(EngineComponents.ViolacaoPreferencial, (LinearExpr)rec, -_o.WeightPreferentialViolation);
                }
                Model.Add(Sum(zs) == Sum(group.Select(x => (IntVar)x.X)));
            }
            Breaks[(e, d, s)] = list;
        }
    }

    // ---------------- R2: no máximo N dias seguidos (N do regime da semana do último dia) ----------------

    private void AddConsecutiveDays()
    {
        foreach (var emp in _ctx.Employees)
        {
            for (var end = 0; end <= LastDay(emp); end++)
            {
                if (_ctx.MaxConsecutiveAt(end) is not { } max) continue;
                var window = Enumerable.Range(end - max.Value, max.Value + 1).ToList();
                var (expr, constant) = Combine(window.Select(k => Worked(emp, k)));
                if (expr is null) continue;
                var rhs = max.Value - constant;
                if (rhs < 0)
                {
                    _warnings.Add($"{emp.Snapshot.Name}: o histórico publicado/outros setores já excedem {max.Value} dias seguidos.");
                    rhs = 0;
                }
                var (group, item) = Continuity(max.Key, window.Where(k => Worked(emp, k).Const == 1 && DayOf(emp, k).Outside));
                Scoped(emp, window[0], end, () => Le(expr, rhs, max.Rigid, max.Key, group: group, item: item));
            }
        }
    }

    // ---------------- R3: folgas na semana civil (regime 6x1/5x2: número exato de folgas) ----------------

    /// <summary>
    /// Número exato de folgas da semana (PlanningContext.RestDays) contado só nos dias normais (não feriado sem autorização
    /// nem ausência); piso legal folgas_minimas_semana; folgas_consecutivas_5x2 (duas folgas em dias seguidos da semana 5x2).
    /// Dia indisponível na disponibilidade semanal é dia normal (vira folga).
    /// </summary>
    private void AddWeeklyRest()
    {
        foreach (var emp in _ctx.Employees)
        {
            for (var w = 0; w < _ctx.Weeks; w++)
            {
                var week = w;
                Scoped(emp, w * 7, w * 7 + 6, () => AddWeeklyRest(emp, week));
            }
        }
    }

    private void AddWeeklyRest(EmployeeInfo emp, int w)
    {
        var rest = _ctx.MinRestDays(w);
        var exact = _ctx.RestDays(w);
        if (exact is null && !_ctx.UsesRegimes)
        {
            if (rest is not { } r) return;
            var (all, allConst) = Combine(Enumerable.Range(w * 7, 7).Select(d => Worked(emp, d)));
            if (all is null) return;
            Le(all, Math.Max(0, 7 - r.Value - allConst), r.Rigid, r.Key);
            return;
        }

        var normal = _ctx.NormalDays(emp, w);
        var (expr, constant) = Combine(normal.Select(d => Worked(emp, d)));
        if (expr is null) return; // semana toda em outro setor/afastado: nada a decidir aqui
        if (rest is { } min) Le(expr, Math.Max(0, normal.Count - min.Value - constant), min.Rigid, min.Key);
        if (exact is not { } regime) return;
        // Semana com mais dias sem trabalho FORÇADO (loja fechada, disponibilidade semanal, sem turno possível) do que o
        // número do regime: esses dias são folga e a pessoa trabalha todos os outros (o número vira o dos forçados) — dia que
        // ela não pode trabalhar nunca torna a semana inviável (mesma conta no avaliador: PlanningContext.WeekRestDays)
        var ex = regime with { Value = _ctx.WeekRestDays(emp, w, regime.Value) };
        // folgas ≥ exato  ⇔  trabalhados ≤ normais − exato
        Le(expr, Math.Max(0, normal.Count - ex.Value - constant), ex.Rigid, ex.Key);
        AddConsecutiveDaysOff(emp, w, normal, Math.Min(regime.Value, normal.Count));
        if (emp.Sectors.Length == 0) return;
        // folgas ≤ exato  ⇔  trabalhados ≥ normais − exato  ⇔  −trabalhados ≤ exato − normais
        var target = Math.Max(0, normal.Count - ex.Value) - constant;
        if (target <= 0) return;
        var sunday = w * 7 + 6;
        if (_ctx.SundayExtraDayOff(w) is not null && normal.Contains(sunday) && Worked(emp, sunday).Var is { } sundayWorked)
        {
            // 6x1: quem folga no domingo pode ter 1 folga a mais (extra ≤ 1 − trabalhou no domingo), penalizada
            var extra = Model.NewBoolVar($"folga_extra_dom_{emp.Index}_{w}");
            Model.Add((LinearExpr)extra + sundayWorked <= 1);
            Le(-1 * expr - (LinearExpr)extra, -target, ex.Rigid, ex.Key);
            if (_stage != SolveStage.CoverageRelaxed) Term(EngineComponents.FolgaExtraDomingo, (LinearExpr)extra, _o.WeightExtraSundayDayOff);
            return;
        }
        Le(-1 * expr, -target, ex.Rigid, ex.Key);
    }

    /// <summary>folgas_consecutivas_5x2: alguma dupla de dias normais seguidos da semana sem trabalho.</summary>
    private void AddConsecutiveDaysOff(EmployeeInfo emp, int week, List<int> normal, int dayOffs)
    {
        if (emp.Sectors.Length == 0 || dayOffs < 2 || _ctx.ConsecutiveDaysOff(week) is not { } rule) return;
        var pairs = new List<IntVar>();
        foreach (var d in normal.Where(d => normal.Contains(d + 1)))
        {
            var a = Worked(emp, d);
            var b = Worked(emp, d + 1);
            if (a.Const == 1 || b.Const == 1) continue;
            var both = Model.NewBoolVar($"par_folga_{emp.Index}_{d}");
            if (a.Var is not null) Model.Add((LinearExpr)both + a.Var <= 1);
            if (b.Var is not null) Model.Add((LinearExpr)both + b.Var <= 1);
            pairs.Add(both);
        }
        if (pairs.Count == 0)
        {
            _warnings.Add($"{emp.Snapshot.Name}: na semana de {_ctx.Dates[week * 7]:dd/MM} não há dois dias seguidos livres para as folgas.");
            Le(LinearExpr.Constant(1), 0, rule.Rigid, rule.Key);
            return;
        }
        Le(-1 * Sum(pairs), -1, rule.Rigid, rule.Key);
    }

    // ---------------- R3b: no máximo N folgas seguidas (max_folgas_seguidas), inclusive na emenda ----------------

    /// <summary>
    /// Folga que conta para max_folgas_seguidas (e para a preferência por folgas intercaladas) no dia <paramref name="d"/>:
    /// null = o dia não pode ser folga de descanso (trabalhado em outro setor/histórico/escala seguinte; ausência ou
    /// feriado sem autorização — interrompem a contagem; fora do horizonte sem folga registrada — não se sabe).
    /// Senão: a variável "trabalhou" do dia (null = folga constante: registrada fora do horizonte ou dia sem turno possível).
    /// </summary>
    private (bool Ok, BoolVar? Y) RestCandidate(EmployeeInfo emp, int d)
    {
        if (d < 0 || d >= _ctx.Days) return (_ctx.RestOutside(emp, d), null);
        if (emp.Busy.ContainsKey(d) || _ctx.NonWorkable(emp, d)) return (false, null);
        return (true, _start[emp.Index, d] is null ? null : Y[emp.Index, d]);
    }

    /// <summary>
    /// max_folgas_seguidas (semântica em PlanningContext.DaysOff, a mesma do avaliador): por pessoa, as sequências de dias
    /// que podem ser folga de descanso (<see cref="PlanningContext.DayOffKindOf"/>) — ausência, feriado sem autorização e
    /// trabalho (outro setor, escala vizinha) interrompem; loja fechada, indisponibilidade na disponibilidade semanal e dia
    /// sem turno possível são NEUTROS (não contam como folga nem interrompem: um dia que a pessoa não pode trabalhar nunca
    /// torna a regra inviável). Em todo trecho de N + 1 desses dias que toca o PERÍODO (N = valor da regra no último dia do
    /// trecho), pelo menos um trabalhado — trecho só com dias de borda/da escala vizinha é dela, como no avaliador. Folga
    /// marcada à mão e folga registrada na escala vizinha contam (constantes); a emenda vira grupo "continuidade" no
    /// diagnóstico e, com o refinamento, cada trecho vira um grupo com a pessoa e os dias.
    /// </summary>
    private void AddMaxDaysOff()
    {
        var (lo, hi) = _ctx.DaysOffRange;
        foreach (var emp in _ctx.Employees)
        {
            if (emp.Sectors.Length == 0) continue; // sem setor na fotografia: não há o que decidir
            var seq = new List<int>();
            var neutral = new List<int>();
            for (var d = lo; d <= hi + 1; d++)
            {
                var kind = d > hi ? DayOffKind.Interrupts : _ctx.DayOffKindOf(emp, d, edgeUnknownIsRest: true);
                if (kind == DayOffKind.Rest) { seq.Add(d); continue; }
                if (kind == DayOffKind.Neutral) { neutral.Add(d); continue; }
                AddMaxDaysOff(emp, seq, neutral);
                seq.Clear();
                neutral.Clear();
            }
        }
    }

    /// <summary>Trechos de N + 1 dias seguidos de uma sequência de possíveis folgas (dias neutros fora da contagem).</summary>
    private void AddMaxDaysOff(EmployeeInfo emp, List<int> seq, List<int> neutral)
    {
        for (var i = 0; i < seq.Count; i++)
        {
            if (_ctx.MaxDaysOffInARow(seq[i]) is not { } max) continue;
            var n = Math.Max(1, max.Value);
            if (i < n) continue;
            var window = seq.GetRange(i - n, n + 1);
            // Trecho sem nenhum dia do período (só dias de borda/da escala vizinha) não é desta escala: o que a escala
            // vizinha registrou lá é dela (o avaliador também só mede trechos que tocam o período) e, sem escala vizinha,
            // os dias de borda só orientam a semana parcial
            if (!_ctx.TouchesPeriod(window[0], window[^1])) continue;
            var vars = window.Where(k => k >= 0 && k < _ctx.Days).Select(k => (IntVar)Y[emp.Index, k]).ToList();
            if (vars.Count == 0) continue;
            // Emenda: folga registrada fora do horizonte ou no dia de borda. Dia de borda sem escala vizinha (EdgeUnknown) é
            // decisão livre do motor: não é emenda (o diagnóstico cita a regra, não a continuidade)
            var outside = window.Where(k => k < 0 || k >= _ctx.Days || (!_ctx.InPeriod(k) && !emp.EdgeUnknown.Contains(k))).ToList();
            var (group, item) = Continuity(max.Key, outside);
            var skipped = neutral.Where(k => k > window[0] && k < window[^1]).ToList();
            // ao menos um dia trabalhado no trecho  ⇔  −Σ trabalhou ≤ −1
            Scoped(emp, window[0], window[^1], () => Le(-1 * Sum(vars), -1, max.Rigid, max.Key, group: group, item: item),
                () => DaysOffItem(emp, window, skipped, max));
        }
    }

    /// <summary>
    /// Item do diagnóstico de um trecho de folgas em conflito: "Ana: folgas em 07/11, 08/11 e 09/11 (máximo 2 seguidas;
    /// 08/11 e 09/11 marcadas manualmente; sem contar 10/11 — loja fechada)". Ausência não aparece (o trecho não a tem).
    /// </summary>
    private DiagnosisItem DaysOffItem(EmployeeInfo emp, List<int> window, List<int> skipped, RuleValue max)
    {
        string D(int k) => _ctx.Start.AddDays(k).ToString("dd/MM");
        string List(IReadOnlyList<int> days) => days.Count == 1 ? D(days[0]) : $"{string.Join(", ", days.Take(days.Count - 1).Select(D))} e {D(days[^1])}";
        var notes = new List<string> { $"máximo {max.Value} seguidas" };
        var manual = window.Where(k => _ctx.Fixed.TryGetValue((emp.Index, k), out var f) && f is null).ToList();
        if (manual.Count > 0) notes.Add($"{List(manual)} {(manual.Count == 1 ? "marcada manualmente" : "marcadas manualmente")}");
        var registered = window.Where(k => !_ctx.InPeriod(k) && (k < 0 || k >= _ctx.Days || emp.EdgeRest.Contains(k))).ToList();
        if (registered.Count > 0) notes.Add($"{List(registered)} de folga na escala {(registered[0] < _ctx.FirstDay ? "anterior" : "seguinte")}");
        var noShift = window.Where(k => k >= 0 && k < _ctx.Days && _ctx.InPeriod(k) && _start[emp.Index, k] is null && !manual.Contains(k)).ToList();
        if (noShift.Count > 0) notes.Add($"{List(noShift)} sem turno possível com o perfil de jornada");
        if (skipped.Count > 0) notes.Add("sem contar " + string.Join(", ", skipped.Select(k => $"{D(k)} ({_ctx.ForcedOffReason(emp, k)})")));
        var dates = window.Where(_ctx.InPeriod).Select(k => _ctx.Dates[k]).ToList();
        return new DiagnosisItem("regra", $"{emp.Snapshot.Name}: folgas em {List(window)} ({string.Join("; ", notes)})", dates,
            emp.Snapshot.EmployeeId, max.Key);
    }

    // ---------------- R4: limite semanal + hora extra explícita ----------------

    private void AddWeeklyHoursAndOvertime()
    {
        foreach (var emp in _ctx.Employees.Where(e => e.Snapshot.JourneyControlled))
        {
            for (var w = 0; w < _ctx.Weeks; w++)
            {
                var minutes = LinearExpr.NewBuilder();
                var dailyExcess = LinearExpr.NewBuilder();
                var minutesConst = 0;
                var excessConst = 0;
                for (var d = w * 7; d < w * 7 + 7; d++)
                {
                    var limit = _ctx.DailyLimit(d)?.Value;
                    if (emp.Busy.TryGetValue(d, out var busy))
                    {
                        minutesConst += busy.PaidMinutes;
                        if (limit is { } lb) excessConst += Math.Max(0, busy.PaidMinutes - lb);
                    }
                    foreach (var t in _ctx.Shifts)
                    {
                        if (!_byShift.TryGetValue((emp.Index, d, t.Index), out var list)) continue;
                        var excess = limit is { } l ? Math.Max(0, t.PaidMinutes - l) : 0;
                        foreach (var x in list)
                        {
                            minutes.AddTerm((LinearExpr)x, t.PaidMinutes);
                            if (excess > 0) dailyExcess.AddTerm((LinearExpr)x, excess);
                        }
                    }
                }

                var weekly = _ctx.WeeklyLimit(w);
                if (!emp.Snapshot.OvertimeAgreement)
                {
                    // Sem acordo de prorrogação não há hora extra: excesso diário já é tratado por turno (limite diário)
                    if (weekly is { } wl) Scoped(emp, w * 7, w * 7 + 6, () => Le(minutes, wl.Value - minutesConst, wl.Rigid, wl.Key));
                    continue;
                }

                var overtime = Model.NewIntVar(0, 7 * PlanningContext.MinutesPerDay, $"ot_{emp.Index}_{w}");
                Overtime.Add(overtime);
                Model.Add((LinearExpr)overtime - dailyExcess >= excessConst);
                Scoped(emp, w * 7, w * 7 + 6, () =>
                {
                    if (weekly is { } limitWeek) Le(minutes - overtime, limitWeek.Value - minutesConst, limitWeek.Rigid, limitWeek.Key);
                    if (_ctx.WeeklyOvertimeCap(emp, w) is { } cap) Le((LinearExpr)overtime, cap.Value, cap.Rigid, cap.Key);
                });
                if (_stage != SolveStage.CoverageRelaxed && emp.CostPerHour is { } cost && cost > 0)
                    Term(EngineComponents.Custo, (LinearExpr)overtime, CostCentsPerMinute(cost) * _o.OvertimePremiumPercent / 100 * _o.WeightCostPerCent);
            }
        }
    }

    private static long CostCentsPerMinute(decimal costPerHour) => (long)Math.Round(costPerHour * 100m / 60m, MidpointRounding.AwayFromZero);

    // ---------------- R5: interjornada (só com controle de jornada) ----------------

    /// <summary>
    /// Entre dois dias trabalhados seguidos: entrada de amanhã + 24h − saída de hoje ≥ interjornada, condicionado aos dois
    /// dias trabalhados (uma restrição por par de dias, com as variáveis de entrada/saída do dia).
    /// </summary>
    private void AddInterShiftRest()
    {
        foreach (var emp in _ctx.Employees.Where(e => e.Snapshot.JourneyControlled))
        {
            for (var d = -1; d < Math.Min(_ctx.Days, LastDay(emp)); d++)
            {
                if (d + 1 > LastDay(emp) || _ctx.InterShiftRest(d + 1) is not { } rest) continue;
                var a = DayOf(emp, d);
                var b = DayOf(emp, d + 1);
                if (a.Off || b.Off) continue;
                if (!a.IsVar && !b.IsVar)
                {
                    if (PlanningContext.Gap(a.CEnd, b.CStart) < rest.Value && _ctx.InPeriod(d) && _ctx.InPeriod(d + 1))
                        _warnings.Add($"{emp.Snapshot.Name}: outro setor já descumpre a interjornada em {_ctx.Start.AddDays(d + 1):dd/MM}.");
                    continue;
                }
                // fim(a) − entrada(b) ≤ 24h − interjornada
                var expr = LinearExpr.NewBuilder();
                var rhs = (long)PlanningContext.MinutesPerDay - rest.Value;
                if (a.IsVar) expr.Add((LinearExpr)a.End!); else rhs -= a.CEnd;
                if (b.IsVar) expr.AddTerm((LinearExpr)b.Start!, -1); else rhs += b.CStart;
                var when = new[] { a.Y, b.Y }.OfType<BoolVar>().Cast<ILiteral>().ToArray();
                var (group, item) = Continuity(rest.Key, new[] { (d, a), (d + 1, b) }.Where(x => x.Item2.Outside).Select(x => x.Item1));
                Scoped(emp, d, d + 1, () => Le(expr, rhs, rest.Rigid, rest.Key, when, group, item));
            }
        }
    }

    // ---------------- R5b: folga isolada precisa de folga_isolada_horas_livres ----------------

    private void AddIsolatedDayOffRest()
    {
        foreach (var emp in _ctx.Employees)
        {
            // m = dia de folga entre dois dias trabalhados (m − 1 e m + 1)
            for (var m = -1; m <= Math.Min(_ctx.Days, LastDay(emp) - 1); m++)
            {
                if (_ctx.IsolatedDayOffFree(m) is not { } rule) continue;
                var off = DayOf(emp, m);
                if (off.ConstWorked) continue; // trabalhou (histórico/outro setor/escala seguinte): não é folga
                var a = DayOf(emp, m - 1);
                var b = DayOf(emp, m + 1);
                if (a.Off || b.Off) continue;
                if (!a.IsVar && !b.IsVar && !off.IsVar) continue;
                // a e b trabalhados e m de folga: 24h + entrada(b) + 24h − fim(a) ≥ horas livres
                var expr = LinearExpr.NewBuilder();
                var rhs = 2L * PlanningContext.MinutesPerDay - rule.Value;
                if (a.IsVar) expr.Add((LinearExpr)a.End!); else rhs -= a.CEnd;
                if (b.IsVar) expr.AddTerm((LinearExpr)b.Start!, -1); else rhs += b.CStart;
                var when = new List<ILiteral>();
                if (a.Y is { } ya) when.Add(ya);
                if (b.Y is { } yb) when.Add(yb);
                if (off.Y is { } yo) when.Add(yo.Not());
                if (!a.IsVar && !b.IsVar)
                {
                    // Só a folga é decisão: se as constantes já não deixam horas livres, a pessoa trabalha no dia m
                    if (PlanningContext.MinutesPerDay + PlanningContext.Gap(a.CEnd, b.CStart) >= rule.Value) continue;
                }
                var (group, item) = Continuity(rule.Key, new[] { (m - 1, a), (m + 1, b) }.Where(x => x.Item2.Outside).Select(x => x.Item1));
                Scoped(emp, m - 1, m + 1, () => Le(expr, rhs, rule.Rigid, rule.Key, when.ToArray(), group, item));
            }
        }
    }

    // ---------------- R6: domingos (Lei 10.101/2000 e, quando ativado, CLT art. 386) ----------------

    private void AddSundayRules()
    {
        foreach (var emp in _ctx.Employees)
        {
            var sundays = Enumerable.Range(0, LastDay(emp) + 1).Where(d => _ctx.Start.AddDays(d).DayOfWeek == DayOfWeek.Sunday).ToList();
            foreach (var s in sundays)
            {
                if (_ctx.SundayWindow(s) is { } window) AddSundayWindow(emp, s, window);
                if (emp.Snapshot.SundayRule == SundayRule.Biweekly && _ctx.BiweeklyWindow(s) is { } biweekly) AddSundayWindow(emp, s, biweekly);
            }
        }
    }

    /// <summary>Na janela de N domingos que termina em <paramref name="sunday"/>, no máximo N − 1 trabalhados.</summary>
    private void AddSundayWindow(EmployeeInfo emp, int sunday, RuleValue window)
    {
        var n = Math.Max(1, window.Value);
        var days = Enumerable.Range(0, n).Select(k => sunday - 7 * k).ToList();
        var (expr, constant) = Combine(days.Select(k => Worked(emp, k)));
        if (expr is null) return;
        var rhs = n - 1 - constant;
        if (rhs < 0)
        {
            _warnings.Add($"{emp.Snapshot.Name}: o histórico publicado já descumpre a regra de domingos.");
            rhs = 0;
        }
        var (group, item) = Continuity(window.Key, days.Where(k => !_ctx.InPeriod(k) && Worked(emp, k).Const == 1));
        Scoped(emp, days[^1], sunday, () => Le(expr, rhs, window.Rigid, window.Key, group: group, item: item));
    }

    // ---------------- R7: cobertura ----------------

    /// <summary>
    /// Presentes na faixa: Σ turnos que cobrem a faixa inteira − Σ posições de intervalo que tocam a faixa (de turnos que
    /// cobrem a faixa). Mínimo RÍGIDO (meta no setor de 2 pessoas e nos dias de borda: <see cref="TargetWeight"/>; fora do
    /// diagnóstico de cobertura relaxada); acima dele, déficit (reforço de pico) e excedente (baixa demanda) na otimização.
    /// </summary>
    private void AddCoverage()
    {
        foreach (var slot in _ctx.Slots)
        {
            var present = LinearExpr.NewBuilder();
            var terms = 0;
            if (_byDaySector.TryGetValue((slot.Day, slot.Sector), out var list))
            {
                foreach (var (e, t, x) in list.Where(v => PlanningContext.Covers(_ctx.Shifts[v.T], slot)))
                {
                    present.Add((LinearExpr)x);
                    terms++;
                }
                foreach (var e in list.Select(v => v.E).Distinct())
                {
                    if (!Breaks.TryGetValue((e, slot.Day, slot.Sector), out var breaks)) continue;
                    var shifts = _breakShifts[(e, slot.Day, slot.Sector)];
                    foreach (var b in breaks)
                    {
                        if (b.Start >= slot.SlotEnd || b.Start + b.Minutes <= slot.SlotStart) continue;
                        // Só desconta posição de turno que cobre a faixa (senão a pessoa nem foi contada)
                        if (!shifts.Any(x => x.T.Snapshot.BreakMinutes == b.Minutes && x.Legal.Contains(b.Start) && PlanningContext.Covers(x.T, slot))) continue;
                        present.AddTerm((LinearExpr)b.Var, -1);
                    }
                }
            }
            IntVar? m = null;
            var hard = _ctx.HardCoverage(slot);
            if (slot.Minimum > 0 && _stage == SolveStage.CoverageRelaxed && !hard)
            {
                // Meta (setor de 2 pessoas / quadro insuficiente / dia de borda): nunca causa inviabilidade, fica fora do diagnóstico
            }
            else if (slot.Minimum > 0 && _stage == SolveStage.Generation && !hard)
            {
                // Meta: falta medida e penalizada (tempo descoberto, com as prioridades de TargetWeight)
                m = Model.NewIntVar(0, slot.Minimum, $"m_{slot.Sector}_{slot.Day}_{slot.SlotStart}");
                Model.Add(present + m >= slot.Minimum);
                TargetDeficits.Add((slot, m));
                Term(EngineComponents.FaltaCoberturaMeta, (LinearExpr)m, TargetWeight(slot));
            }
            else if (slot.Minimum > 0)
            {
                if (_stage == SolveStage.Generation)
                {
                    if (terms == 0)
                    {
                        // Ninguém pode estar na faixa: conflito explícito (o diagnóstico de capacidade já explica)
                        var c0 = Model.Add(LinearExpr.Constant(0) >= slot.Minimum);
                        Guard(c0, CoverageGroup(slot), () => CoverageItem(slot));
                    }
                    else
                    {
                        var c = Model.Add(present >= slot.Minimum);
                        Guard(c, CoverageGroup(slot), () => CoverageItem(slot));
                    }
                }
                else
                {
                    m = Model.NewIntVar(0, slot.Minimum, $"m_{slot.Sector}_{slot.Day}_{slot.SlotStart}");
                    Model.Add(present + m >= slot.Minimum);
                    if (_stage == SolveStage.CoverageRelaxed) Deficits.Add((slot, m));
                    else Term(EngineComponents.FaltaCoberturaMinima, (LinearExpr)m, _o.WeightCoverageDeficitRigid);
                }
            }
            if (_stage == SolveStage.CoverageRelaxed) continue;

            if (slot.Required > slot.Minimum)
            {
                // Acima do mínimo; com o mínimo medido (meta/correção), a falta dele soma (senão o reforço o tornaria rígido)
                var u = Model.NewIntVar(0, slot.Required - slot.Minimum, $"u_{slot.Sector}_{slot.Day}_{slot.SlotStart}");
                if (m is null) Model.Add(present + u >= slot.Required);
                else Model.Add(present + u + m >= slot.Required);
                Deficits.Add((slot, u));
                Term(EngineComponents.FaltaPico, (LinearExpr)u, PeakWeight(slot));
            }
            if (_ctx.OperationMode && _o.WeightLowDemandExcess > 0 && terms > 0)
            {
                var v = Model.NewIntVar(0, Math.Max(0, terms), $"v_{slot.Sector}_{slot.Day}_{slot.SlotStart}");
                Model.Add(present - v <= slot.Required);
                Term(EngineComponents.ExcedenteBaixaDemanda, (LinearExpr)v, _o.WeightLowDemandExcess);
            }
        }
    }

    private string CoverageGroup(CoverageSlot slot) => $"cobertura:{slot.Day}:{slot.MinimumKey}";

    private Dictionary<(int Day, int Sector), (int Open, int Close)>? _dayRanges;

    /// <summary>
    /// Peso de cada pessoa faltando no mínimo de uma faixa META (<see cref="PlanningContext.HardCoverage"/>), proporcional ao
    /// tamanho da faixa (base: 30 min). Dia de borda: <see cref="SolverOptions.WeightEdgeCoverageDeficit"/> (só orienta a
    /// semana parcial). Setor de 2 pessoas ou com quadro insuficiente: <see cref="SolverOptions.WeightCoverageTargetDeficit"/> + adicional na primeira e
    /// na última faixa do dia (abertura/fechamento) + desempate crescente para a ponta do dia preferida
    /// (<see cref="SolverOptions.CoverageTargetSinglePreference"/>). Pico manual e demanda acima do mínimo somam o peso do
    /// reforço (termo u da faixa): ficam à frente de abertura/fechamento.
    /// </summary>
    private long TargetWeight(CoverageSlot slot)
    {
        var minutes = Math.Max(1, slot.SlotMinutes);
        if (!_ctx.InPeriod(slot.Day)) return Math.Max(1, (long)_o.WeightEdgeCoverageDeficit * minutes / 30);
        var weight = (long)_o.WeightCoverageTargetDeficit * minutes / 30;
        _dayRanges ??= _ctx.Slots.GroupBy(s => (s.Day, s.Sector))
            .ToDictionary(g => g.Key, g => (g.Min(s => s.SlotStart), g.Max(s => s.SlotEnd)));
        var (open, close) = _dayRanges[(slot.Day, slot.Sector)];
        if (slot.SlotStart == open || slot.SlotEnd == close) weight += _o.WeightCoverageTargetOpeningClosing;
        if (close > open && _o.WeightCoverageTargetTilt > 0)
        {
            var closing = string.Equals(_o.CoverageTargetSinglePreference, "fechamento", StringComparison.OrdinalIgnoreCase);
            var distance = closing ? close - slot.SlotEnd : slot.SlotStart - open;
            weight += (long)_o.WeightCoverageTargetTilt * (close - open - distance) / (close - open);
        }
        return Math.Max(1, weight);
    }

    /// <summary>
    /// Peso de cada pessoa faltando acima do mínimo na faixa: pico manual (cadastrado pelo usuário) →
    /// WeightManualPeakDeficit (nunca abaixo do peso do reforço); com operação → WeightPeakDeficit; sem →
    /// WeightCoverageDeficitPreferential.
    /// </summary>
    private int PeakWeight(CoverageSlot slot)
    {
        var standard = _ctx.OperationMode ? _o.WeightPeakDeficit : _o.WeightCoverageDeficitPreferential;
        return slot.ManualPeak is null ? standard : Math.Max(standard, _o.WeightManualPeakDeficit);
    }

    private DiagnosisItem CoverageItem(CoverageSlot slot)
    {
        var date = _ctx.Dates[slot.Day];
        var what = slot.MinimumKey switch
        {
            RuleKeys.MinimoAbertura => $"Mínimo de {slot.Minimum} pessoa(s) na abertura",
            RuleKeys.MinimoFechamento => $"Mínimo de {slot.Minimum} pessoa(s) no fechamento",
            RuleKeys.CoberturaMinima => $"Cobertura mínima de {slot.Minimum} pessoa(s) fora do intervalo",
            _ => "Cobertura da demanda (regra rígida)"
        };
        return new DiagnosisItem("cobertura", $"{what} em {date:dd/MM} ({DayName(date)})", [date], Chave: slot.MinimumKey);
    }

    internal static string DayName(DateOnly d) => d.DayOfWeek switch
    {
        DayOfWeek.Monday => "seg",
        DayOfWeek.Tuesday => "ter",
        DayOfWeek.Wednesday => "qua",
        DayOfWeek.Thursday => "qui",
        DayOfWeek.Friday => "sex",
        DayOfWeek.Saturday => "sáb",
        _ => "dom"
    };

    // ---------------- Setor de 2 pessoas: folgas fixas e intercaladas ----------------

    /// <summary>
    /// Folgas fixas e intercaladas (<see cref="PlanningContext.FixedDaysOffPairs"/>; decisões documentadas no
    /// PlanningContext.FixedDaysOff). Por pessoa do par:
    /// <list type="bullet">
    /// <item>P[0..6]: padrão semanal; Σ P = número de folgas da semana (o maior do período: 2 no 5x2). Dia da semana com a
    /// loja sempre fechada (ou em que a pessoa nunca tem turno possível, ex.: disponibilidade) entra no padrão.</item>
    /// <item>Intercaladas: P dos dois sem dia em comum; sem folgas_consecutivas_5x2 ativa, P sem dias vizinhos (inclusive
    /// domingo–segunda) e, com o rodízio de domingos (loja aberta no domingo e regra de domingos ativa), sem segunda e com o
    /// sábado como dia de troca; em todo dia aberto em que os dois podem trabalhar, pelo menos um trabalha (rígido).</item>
    /// <item>Fixas: em semana regular do par (sem ausência, feriado, célula manual nem dia da escala vizinha de nenhum
    /// dos dois), dia de segunda a sábado fora do padrão é trabalhado (rígido). Domingo fica
    /// livre para o rodízio da regra de domingos: na semana do domingo de folga a pessoa trabalha um dia do padrão.</item>
    /// <item>Penalidades (WeightFixedDayOffDeviation = D): trabalhar num dia do padrão custa D (D/2 no dia de troca Q, um
    /// só por pessoa — o domingo de folga troca sempre o mesmo dia); folga fora do padrão (semana irregular) custa D, menos
    /// no domingo. Folga/trabalho registrados na escala vizinha (dias de borda e 7 dias antes/depois do horizonte) puxam o
    /// padrão (WeightFixedDayOffContinuity por dia): o mesmo padrão de um mês para o outro.</item>
    /// </list>
    /// As regras legais (folgas exatas do regime, dias seguidos, domingos, interjornada...) continuam como estão: o padrão
    /// só restringe a escolha dos dias.
    /// </summary>
    private void AddFixedDaysOff()
    {
        var restDays = Enumerable.Range(0, _ctx.Weeks).Select(w => (_ctx.RestDays(w) ?? _ctx.MinRestDays(w))?.Value).OfType<int>().ToList();
        var k = Math.Clamp(restDays.Count > 0 ? restDays.Max() : 2, 1, 3);
        var noAdjacency = Enumerable.Range(0, _ctx.Weeks).Any(w => _ctx.ConsecutiveDaysOff(w) is not null);
        int Dow(int d) => PlanningContext.WeekdayIndex(_ctx.Dates[d].DayOfWeek);
        // Dia da semana com a loja fechada em todo o horizonte (horário de funcionamento): folga de todos, no padrão
        var closedDow = Enumerable.Range(0, 7).Select(i => Enumerable.Range(0, _ctx.Days).Where(d => Dow(d) == i).All(_ctx.IsClosed)).ToArray();
        // Rodízio de domingos: a loja abre no domingo e há regra de domingo de folga (a cada N semanas) no horizonte
        var sundayRotation = !closedDow[6] && Enumerable.Range(0, _ctx.Days).Where(_ctx.IsSunday)
            .Any(d => _ctx.SundayWindow(d) is not null || _ctx.BiweeklyWindow(d) is not null);
        var deviation = (long)Math.Max(0, _o.WeightFixedDayOffDeviation);
        var continuity = (long)Math.Max(0, _o.WeightFixedDayOffContinuity);
        const string component = EngineComponents.FolgasFixas;

        foreach (var pair in _ctx.FixedDaysOffPairs)
        {
            var people = pair.People.ToList();
            // Dias em que o padrão decide (variável com turno possível, no horizonte, sem célula manual, loja aberta)
            var decides = new Dictionary<int, HashSet<int>>();
            var forced = new Dictionary<int, bool[]>();
            var ok = true;
            foreach (var emp in people)
            {
                var days = new HashSet<int>();
                var structural = new bool[7];
                for (var i = 0; i < 7; i++)
                {
                    var candidates = Enumerable.Range(0, _ctx.Days).Where(d => Dow(d) == i && Candidate(emp, d)).ToList();
                    // Sem turno possível em nenhuma ocorrência do dia da semana (ex.: indisponível na disponibilidade semanal)
                    structural[i] = closedDow[i] || (candidates.Count > 0 && candidates.All(d => _start[emp.Index, d] is null));
                    foreach (var d in candidates.Where(d => _start[emp.Index, d] is not null)) days.Add(d);
                }
                decides[emp.Index] = days;
                forced[emp.Index] = structural;
                if (structural.Count(x => x) > k) ok = false;
            }
            if (!ok)
            {
                _warnings.Add($"Setor {_ctx.Sectors[pair.Sector].Name}: folgas fixas não aplicadas — há mais dias sem turno possível na semana do que folgas.");
                continue;
            }

            // Semana regular do par: todo dia, dos dois, é decidido pelo padrão ou é estrutural (loja fechada / sem turno
            // possível). Com ausência, feriado, célula manual ou dia da escala vizinha de qualquer um dos dois, a semana é
            // irregular para o par (quem fica cobre o outro): o padrão vira só penalidade
            bool RegularDay(EmployeeInfo emp, int d) => decides[emp.Index].Contains(d) || (forced[emp.Index][Dow(d)] && Candidate(emp, d));
            var regularWeeks = Enumerable.Range(0, _ctx.Weeks)
                .Where(w => Enumerable.Range(w * 7, 7).All(d => people.All(emp => RegularDay(emp, d)))).ToHashSet();

            foreach (var emp in people)
            {
                var p = Enumerable.Range(0, 7).Select(i => Model.NewBoolVar($"fixa_{emp.Index}_{i}")).ToArray();
                var q = Enumerable.Range(0, 7).Select(i => Model.NewBoolVar($"troca_{emp.Index}_{i}")).ToArray();
                FixedPatterns[emp.Index] = (p, q);
                Model.Add(Sum(p) == k);
                Model.Add(Sum(q) <= 1);
                Model.Add((LinearExpr)q[6] == 0);
                for (var i = 0; i < 7; i++)
                {
                    Model.Add((LinearExpr)q[i] <= (LinearExpr)p[i]);
                    if (forced[emp.Index][i]) Model.Add((LinearExpr)p[i] == 1);
                }
                if (!noAdjacency)
                {
                    for (var i = 0; i < 7; i++)
                    {
                        var j = (i + 1) % 7;
                        if (!forced[emp.Index][i] && !forced[emp.Index][j]) Model.Add((LinearExpr)p[i] + p[j] <= 1);
                    }
                    if (sundayRotation)
                    {
                        // Domingo de folga em rodízio: a segunda não entra no padrão (domingo + segunda seriam folgas seguidas)
                        // e, com o sábado no padrão, o sábado é o dia de troca (trabalha no sábado da semana do domingo)
                        if (!forced[emp.Index][0]) Model.Add((LinearExpr)p[0] == 0);
                        Model.Add((LinearExpr)q[5] >= (LinearExpr)p[5]);
                    }
                }

                var mine = decides[emp.Index];
                for (var w = 0; w < _ctx.Weeks; w++)
                {
                    var week = Enumerable.Range(w * 7, 7).ToList();
                    var regular = regularWeeks.Contains(w);
                    foreach (var d in week.Where(mine.Contains))
                    {
                        var i = Dow(d);
                        var y = Y[emp.Index, d];
                        if (regular && i != 6) Model.Add((LinearExpr)y + p[i] >= 1); // fora do padrão: trabalha
                        if (deviation == 0) continue;
                        // Trabalho em dia do padrão: D (D/2 no dia de troca)
                        var worked = Model.NewBoolVar($"fixa_trab_{emp.Index}_{d}");
                        Model.Add((LinearExpr)worked >= (LinearExpr)y + p[i] - 1);
                        Term(component, (LinearExpr)worked, deviation);
                        if (i != 6)
                        {
                            var swap = Model.NewBoolVar($"fixa_troca_{emp.Index}_{d}");
                            Model.Add((LinearExpr)swap <= (LinearExpr)worked);
                            Model.Add((LinearExpr)swap <= (LinearExpr)q[i]);
                            Term(component, (LinearExpr)swap, -deviation / 2);
                            // Folga fora do padrão (semana irregular; o domingo é livre)
                            var off = Model.NewBoolVar($"fixa_folga_{emp.Index}_{d}");
                            Model.Add((LinearExpr)off >= 1 - (LinearExpr)y - p[i]);
                            Term(component, (LinearExpr)off, deviation);
                        }
                    }
                }

                // Continuidade: o padrão segue a folga/trabalho registrados na escala vizinha (o domingo é rodízio)
                if (continuity == 0) continue;
                for (var d = -7; d < _ctx.Days + 7; d++)
                {
                    if (d >= 0 && d < _ctx.Days && _ctx.InPeriod(d)) continue;
                    var date = _ctx.Start.AddDays(d);
                    var i = PlanningContext.WeekdayIndex(date.DayOfWeek);
                    if (i == 6 || forced[emp.Index][i] || _ctx.IsClosed(d)) continue;
                    if (_ctx.RestOutside(emp, d)) Penalty(component, (LinearExpr)p[i], continuity);
                    else if (OutsideWorked(emp, d)) Term(component, (LinearExpr)p[i], continuity);
                }
            }

            // Intercaladas: padrões sem dia em comum e, em todo dia aberto em que os dois podem trabalhar, alguém trabalha
            var (a, b) = (people[0], people[1]);
            for (var i = 0; i < 7; i++)
                if (!(forced[a.Index][i] && forced[b.Index][i]))
                    Model.Add((LinearExpr)FixedPatterns[a.Index].P[i] + FixedPatterns[b.Index].P[i] <= 1);
            foreach (var d in decides[a.Index].Intersect(decides[b.Index]))
                Model.Add((LinearExpr)Y[a.Index, d] + Y[b.Index, d] >= 1);
        }

        // Dia do horizonte em que o padrão pode valer: disponível, fora de outro setor, sem célula manual, loja aberta e
        // que não é dia de borda já registrado na escala vizinha
        bool Candidate(EmployeeInfo emp, int d) =>
            !emp.Busy.ContainsKey(d) && !_ctx.NonWorkable(emp, d) && !_ctx.Fixed.ContainsKey((emp.Index, d))
            && !emp.EdgeRest.Contains(d) && (!_ctx.IsClosed(d) || closedDow[Dow(d)]);
    }

    /// <summary>Trabalho registrado fora do período (dia de borda ou fora do horizonte) na escala vizinha.</summary>
    private bool OutsideWorked(EmployeeInfo emp, int d)
    {
        if (d >= 0 && d < _ctx.Days) return !_ctx.InPeriod(d) && emp.Busy.ContainsKey(d);
        return DayOf(emp, d).ConstWorked;
    }

    /// <summary>
    /// Dica para a busca no setor de 2 pessoas com cobertura como meta: um abre e o outro fecha (o primeiro do par com o
    /// turno que começa mais cedo, o segundo com o que termina mais tarde; os demais turnos com dica 0). É a forma que
    /// cobre mais tempo com a interjornada e a folga isolada (quem fecha às 22:00 não abre no dia seguinte) — o CP-SAT chega
    /// a ela muito mais rápido com a dica. Só orienta: a folga (y) fica livre e a solução final é a do objetivo.
    /// </summary>
    private void AddCoverageTargetHints()
    {
        foreach (var pair in _ctx.FixedDaysOffPairs)
        {
            foreach (var (emp, opens) in new[] { (pair.A, true), (pair.B, false) })
            {
                for (var d = 0; d < _ctx.Days; d++)
                {
                    if (_ctx.Fixed.ContainsKey((emp.Index, d))) continue;
                    var vars = X.Where(kv => kv.Key.E == emp.Index && kv.Key.D == d && kv.Key.S == pair.Sector).ToList();
                    if (vars.Count == 0) continue;
                    var best = opens
                        ? vars.MinBy(kv => (_ctx.Shifts[kv.Key.T].StartMin, -_ctx.Shifts[kv.Key.T].EndMin)).Key.T
                        : vars.MinBy(kv => (-_ctx.Shifts[kv.Key.T].EndMin, _ctx.Shifts[kv.Key.T].StartMin)).Key.T;
                    foreach (var (key, x) in vars)
                        if (key.T != best) Hint(x, 0L);
                }
            }
        }
        if (_o.CoverageTargetHints) AddUnderstaffedHints();
    }

    /// <summary>
    /// Dica para a busca no setor com cobertura como meta por QUADRO INSUFICIENTE (3+ pessoas; o de 2 pessoas tem a dica
    /// acima). Sem ela o CP-SAT tende a parar numa primeira escala ruim (todos no mesmo horário e de folga nos mesmos dias).
    /// Escala de partida: folgas espalhadas na semana (cada pessoa nos dias da semana com menos folgas até ali; seguidas com
    /// folgas_consecutivas_5x2, senão sem dias vizinhos) e, em cada dia, um abre (de preferência quem tem perfil Abertura e
    /// não fechou na véspera, pela interjornada), outro fecha (de preferência Fechamento) e os demais fazem horários
    /// intermediários espalhados pelo expediente (com um só, o mais perto do meio) — o que cobre os intervalos de quem abre
    /// e de quem fecha —, com os intervalos revezados (cada um quando há outra pessoa no turno). Só orienta: as regras
    /// rígidas e o objetivo decidem a escala final (células manuais ficam como estão). O CP-SAT acha poucas soluções num mês
    /// nesse tempo, então a partida pesa; o domingo de folga em rodízio na dica foi testado e saiu pior (o motor corrige a
    /// dica melhor sem ele).
    /// </summary>
    private void AddUnderstaffedHints()
    {
        var pairs = _ctx.FixedDaysOffPairs.Select(p => p.Sector).ToHashSet();
        var restDays = Enumerable.Range(0, _ctx.Weeks).Select(w => (_ctx.RestDays(w) ?? _ctx.MinRestDays(w))?.Value).OfType<int>().ToList();
        var k = Math.Clamp(restDays.Count > 0 ? restDays.Max() : 2, 1, 3);
        var consecutive = Enumerable.Range(0, _ctx.Weeks).Any(w => _ctx.ConsecutiveDaysOff(w) is not null);
        foreach (var sector in _ctx.CoverageTargetSectors.Where(s => !pairs.Contains(s)).Order())
        {
            var people = _ctx.Employees.Where(e => e.Sectors.Contains(sector) && _ctx.AvailableInPeriod(e)).ToList();
            if (people.Count == 0) continue;

            // Folgas espalhadas: cada pessoa nos dias da semana com menos folgas até ali
            var load = new int[7];
            var offDays = new Dictionary<int, HashSet<int>>();
            foreach (var e in people)
            {
                int[] best;
                if (k == 2)
                {
                    // Duas folgas: seguidas (folgas_consecutivas_5x2) ou sem dias vizinhos (folgas intercaladas preferidas)
                    var options = consecutive
                        ? Enumerable.Range(0, 6).Select(i => new[] { i, i + 1 })
                        : Enumerable.Range(0, 7).SelectMany(i => Enumerable.Range(i + 2, Math.Max(0, 5 - i)).Where(j => !(i == 0 && j == 6))
                            .Select(j => new[] { i, j }));
                    best = options.MinBy(o => (o.Sum(i => load[i]), o[0]))!;
                }
                else best = Enumerable.Range(0, 7).OrderBy(i => load[i]).ThenBy(i => i).Take(k).ToArray();
                foreach (var i in best) load[i]++;
                offDays[e.Index] = best.ToHashSet();
            }
            var byDay = X.Where(kv => people.Any(p => p.Index == kv.Key.E)).GroupBy(kv => (kv.Key.E, kv.Key.D))
                .ToDictionary(g => g.Key, g => g.ToList());

            // Papel preferido pelo perfil: 0 = abre, 1 = intermediário, 2 = fecha
            static int Role(EmployeeInfo e) => e.Profile switch
            {
                JourneyProfiles.Abertura => 0,
                JourneyProfiles.Fechamento => 2,
                _ => 1
            };
            var closedYesterday = new HashSet<int>();
            for (var d = 0; d < _ctx.Days; d++)
            {
                var closedToday = new HashSet<int>();
                var dow = PlanningContext.WeekdayIndex(_ctx.Dates[d].DayOfWeek);
                var workers = new List<EmployeeInfo>();
                foreach (var e in people)
                {
                    if (_ctx.Fixed.ContainsKey((e.Index, d)) || _start[e.Index, d] is null) continue;
                    var off = offDays[e.Index].Contains(dow);
                    Hint(Y[e.Index, d], off ? 0L : 1L);
                    if (!off) workers.Add(e);
                }
                if (!DayRange(d, sector, out var open, out var close))
                {
                    closedYesterday = closedToday;
                    continue;
                }
                var opener = workers.Where(e => !closedYesterday.Contains(e.Index)).OrderBy(Role).FirstOrDefault();
                var closer = workers.Where(e => e != opener).OrderByDescending(Role).FirstOrDefault();
                var middle = (open + close) / 2;
                // Intermediários espalhados entre a abertura e o fechamento (com um só, no meio do expediente)
                var middles = workers.Where(e => e != opener && e != closer).ToList();
                var chosenToday = new List<(EmployeeInfo E, ShiftInfo T)>();
                foreach (var e in workers)
                {
                    if (!byDay.TryGetValue((e.Index, d), out var vars)) continue;
                    var mine = vars.Where(kv => kv.Key.S == sector).ToList();
                    if (mine.Count == 0) continue;
                    int Out(ShiftInfo t) => _ctx.OutOfProfile(e, t) ? 1 : 0;
                    var target = middles.Count == 0 ? middle : open + (close - open) * (middles.IndexOf(e) + 1) / (middles.Count + 1);
                    var chosen = e == opener
                        ? mine.MinBy(kv => (_ctx.Shifts[kv.Key.T].StartMin, -_ctx.Shifts[kv.Key.T].EndMin, Out(_ctx.Shifts[kv.Key.T])))
                        : e == closer
                            ? mine.MinBy(kv => (-_ctx.Shifts[kv.Key.T].EndMin, _ctx.Shifts[kv.Key.T].StartMin, Out(_ctx.Shifts[kv.Key.T])))
                            : mine.MinBy(kv => (Math.Abs((_ctx.Shifts[kv.Key.T].StartMin + _ctx.Shifts[kv.Key.T].EndMin) / 2 - target),
                                Out(_ctx.Shifts[kv.Key.T])));
                    foreach (var (key, x) in vars) Hint(x, key == chosen.Key ? 1L : 0L);
                    chosenToday.Add((e, _ctx.Shifts[chosen.Key.T]));
                    if (_ctx.Shifts[chosen.Key.T].EndMin >= close) closedToday.Add(e.Index);
                }
                HintBreaks(d, sector, chosenToday);
                closedYesterday = closedToday;
            }
        }

        // Intervalos de partida revezados: pela entrada, cada um na posição legal mais perto do meio do turno que não cruza
        // outro intervalo e em que outra pessoa do dia está no turno (o setor não fica vazio)
        void HintBreaks(int d, int s, List<(EmployeeInfo E, ShiftInfo T)> today)
        {
            var taken = new List<(int Start, int End)>();
            foreach (var (e, t) in today.OrderBy(x => x.T.StartMin))
            {
                var minutes = t.Snapshot.BreakMinutes;
                if (minutes <= 0 || !Breaks.TryGetValue((e.Index, d, s), out var vars) || vars.Count == 0) continue;
                var legal = _breakShifts[(e.Index, d, s)].FirstOrDefault(x => x.T == t).Legal;
                if (legal is null || legal.Count == 0) continue;
                var mid = (t.StartMin + t.EndMin) / 2;
                bool Free(int p) => taken.All(b => p + minutes <= b.Start || p >= b.End);
                bool Covered(int p) => today.Any(o => o.E != e && o.T.StartMin <= p && p + minutes <= o.T.EndMin);
                var ordered = legal.OrderBy(p => Math.Abs(p + minutes / 2 - mid)).ThenBy(p => p).ToList();
                var start = ordered.Where(p => Free(p) && Covered(p)).Concat(ordered.Where(Free)).Concat(ordered).First();
                taken.Add((start, start + minutes));
                foreach (var b in vars) Hint(b.Var, b.Start == start && b.Minutes == minutes ? 1L : 0L);
            }
        }

        // Expediente do setor no dia (pelas faixas de cobertura); false = sem faixa (loja fechada)
        bool DayRange(int day, int s, out int open, out int close)
        {
            _dayRanges ??= _ctx.Slots.GroupBy(x => (x.Day, x.Sector))
                .ToDictionary(g => g.Key, g => (g.Min(x => x.SlotStart), g.Max(x => x.SlotEnd)));
            if (_dayRanges.TryGetValue((day, s), out var r))
            {
                (open, close) = r;
                return true;
            }
            (open, close) = (0, 0);
            return false;
        }
    }

    private readonly HashSet<int> _hinted = [];

    /// <summary>Dica de valor (uma por variável: a primeira vale).</summary>
    private void Hint(BoolVar x, long value)
    {
        if (_hinted.Add(x.GetIndex())) Model.AddHint((IntVar)x, value);
    }

    /// <summary>Padrão escolhido de cada pessoa (dias da semana) e o dia de troca pelo domingo (null = nenhum).</summary>
    public Dictionary<int, (List<DayOfWeek> Days, DayOfWeek? Swap)> FixedPatternValues(Func<IntVar, long> value) =>
        FixedPatterns.ToDictionary(kv => kv.Key, kv =>
        {
            static DayOfWeek Of(int i) => (DayOfWeek)((i + 1) % 7);
            var days = Enumerable.Range(0, 7).Where(i => value(kv.Value.P[i]) == 1).Select(Of).ToList();
            var swap = Enumerable.Range(0, 7).Where(i => value(kv.Value.Q[i]) == 1).Select(i => (DayOfWeek?)Of(i)).FirstOrDefault();
            return (days, swap);
        });

    // ---------------- Sugestões: mudança mínima ----------------

    private void AddChangePenalty(IReadOnlyDictionary<(int E, int D), int?> baseline)
    {
        foreach (var ((e, d), current) in baseline)
        {
            if (current is { } t)
            {
                // mudou = 1 − X(e,d,t)
                if (ShiftExpr(e, d, t) is { } x) Penalty(EngineComponents.CelulasAlteradas, x, _o.WeightChangedCell);
            }
            else
            {
                Term(EngineComponents.CelulasAlteradas, (LinearExpr)Y[e, d], _o.WeightChangedCell);
            }
        }
    }

    // ---------------- Função objetivo ----------------

    private void AddObjectiveTerms()
    {
        // Minutos pagos (evita escalar gente além da demanda) e hora extra com peso maior
        foreach (var ((_, _, t, _), x) in X) Term(EngineComponents.Custo, (LinearExpr)x, (long)_ctx.Shifts[t].PaidMinutes * _o.WeightPaidMinute);
        foreach (var ot in Overtime) Term(EngineComponents.HorasExtras, (LinearExpr)ot, _o.WeightOvertimeMinute);

        // Custo adicional: adicional noturno (22h às 5h) de quem tem custo/hora
        foreach (var ((e, _, t, _), x) in X)
        {
            if (_ctx.Employees[e].CostPerHour is not { } cost || cost <= 0) continue;
            var night = NightMinutes(_ctx.Shifts[t]);
            if (night > 0) Term(EngineComponents.Custo, (LinearExpr)x, night * CostCentsPerMinute(cost) * _o.NightPremiumPercent / 100 * _o.WeightCostPerCent);
        }

        AddEquity();
        AddRotation();
        AddDayOffPreferences();
        AddDoubleDayOffPriority();
        AddStartChanges();
        AddShiftPreferences();
        if (_stage == SolveStage.Generation) AddBreakStagger();
    }

    /// <summary>
    /// Intervalos escalonados (só na geração): em cada setor × dia, cada 15 min em que mais de uma pessoa está em intervalo
    /// ao mesmo tempo custa WeightBreakOverlapPer15Min por pessoa a mais. Junto com a cobertura — o intervalo que toca uma
    /// faixa desconta a pessoa dela, então o mínimo rígido (ou a meta, no setor com cobertura como meta, em que cada faixa
    /// descoberta custa WeightCoverageTargetDeficit) já põe o intervalo onde outra pessoa do setor está trabalhando —, este
    /// termo faz os intervalos se revezarem quando sobra gente (sem ele, o excedente em baixa demanda até premiava juntar os
    /// intervalos). Só no setor com cobertura como meta por quadro insuficiente: com a cobertura rígida o mínimo (e o
    /// reforço de pico) já garante quem fica no setor e o termo, em meses com muita gente, deixava a busca bem mais lenta
    /// (escalas sem solução no tempo); no de 2 pessoas os dois em intervalo já deixam o setor vazio. As posições possíveis
    /// continuam só as legais (PlanningContext.BreakOptions: regras de posição rígidas; as preferenciais penalizadas à parte).
    /// </summary>
    private void AddBreakStagger()
    {
        var weight = (long)_o.WeightBreakOverlapPer15Min;
        if (weight <= 0) return;
        // Só no setor com cobertura como meta por quadro insuficiente (no de 2 pessoas, os dois em intervalo já deixam o
        // setor vazio e a meta decide; com a cobertura rígida, o mínimo decide e o termo só pesaria a busca de meses inteiros)
        foreach (var group in Breaks.Where(kv => kv.Value.Count > 0 && _ctx.SoftCoverage(kv.Key.S) && !_ctx.TwoPersonCoverage(kv.Key.S))
                     .GroupBy(kv => (kv.Key.D, kv.Key.S)))
        {
            var people = group.ToList();
            if (people.Count < 2) continue;
            var from = people.SelectMany(p => p.Value).Min(b => b.Start) / BreakPlanner.StepMinutes * BreakPlanner.StepMinutes;
            var to = people.SelectMany(p => p.Value).Max(b => b.Start + b.Minutes);
            for (var t = from; t < to; t += BreakPlanner.StepMinutes)
            {
                var point = t;
                // Pessoas que podem estar em intervalo neste quarto de hora (uma soma por pessoa: no máximo 1 intervalo)
                var onBreak = people
                    .Select(p => p.Value.Where(b => b.Start <= point && point < b.Start + b.Minutes).Select(b => (IntVar)b.Var).ToList())
                    .Where(l => l.Count > 0).ToList();
                if (onBreak.Count < 2) continue;
                var extra = Model.NewIntVar(0, onBreak.Count - 1, $"ib_{group.Key.D}_{group.Key.S}_{point}");
                Model.Add((LinearExpr)extra >= Sum(onBreak.SelectMany(l => l)) - 1);
                Term(EngineComponents.IntervalosSimultaneos, (LinearExpr)extra, weight);
            }
        }
    }

    /// <summary>Minutos do turno entre 22h e 5h (adicional noturno, CLT art. 73).</summary>
    private static int NightMinutes(ShiftInfo t)
    {
        var total = 0;
        foreach (var (from, to) in new[] { (0, 5 * 60), (22 * 60, 29 * 60), (46 * 60, 53 * 60) })
            total += Math.Max(0, Math.Min(t.EndMin, to) - Math.Max(t.StartMin, from));
        return total;
    }

    /// <summary>Equilíbrio entre colaboradores: (máximo − mínimo) de domingos, aberturas, fechamentos e folgas no fim de semana.</summary>
    private void AddEquity()
    {
        var sundays = Enumerable.Range(0, _ctx.Days).Where(_ctx.IsSunday).ToList();
        var sundayCounts = _ctx.Employees.Where(e => e.Sectors.Length > 0).Select(e => (LinearExpr?)Sum(sundays.Select(d => (IntVar)Y[e.Index, d]))).ToList();
        if (sundays.Count > 0) _indicators["domingos"] = sundayCounts.OfType<LinearExpr>().ToList();
        Equity(sundayCounts, sundays.Count, _o.WeightSundayEquity, "dom", EngineComponents.EquidadeDomingos);

        // Aberturas/fechamentos: turnos do tipo (com operação) ou o turno que começa mais cedo / termina mais tarde
        var opening = _ctx.OperationMode
            ? _ctx.Shifts.Where(s => s.Kind == JourneyProfiles.Abertura).Select(s => s.Index).ToHashSet()
            : _ctx.Shifts.Where(s => _ctx.Shifts.Length > 1 && s.StartMin == _ctx.Shifts.Min(x => x.StartMin)).Select(s => s.Index).ToHashSet();
        var closing = _ctx.OperationMode
            ? _ctx.Shifts.Where(s => s.Kind == JourneyProfiles.Fechamento).Select(s => s.Index).ToHashSet()
            : _ctx.Shifts.Where(s => _ctx.Shifts.Length > 1 && s.EndMin == _ctx.Shifts.Max(x => x.EndMin)).Select(s => s.Index).ToHashSet();
        foreach (var (set, name) in new[] { (opening, "abre"), (closing, "fecha") })
        {
            if (set.Count == 0) continue;
            // Só turnos do perfil: aberturas/fechamentos em rodízio têm equidade própria (AddRotation)
            var counts = _ctx.Employees.Select(e =>
            {
                var vars = X.Where(kv => kv.Key.E == e.Index && set.Contains(kv.Key.T) && _ctx.ProfileAllows(e, _ctx.Shifts[kv.Key.T]))
                    .Select(kv => (IntVar)kv.Value).ToList();
                return vars.Count == 0 ? null : (LinearExpr?)Sum(vars);
            }).ToList();
            _indicators[name == "abre" ? "aberturas" : "fechamentos"] = counts.OfType<LinearExpr>().ToList();
            Equity(counts, _ctx.Days, _o.WeightOpeningClosingEquity, name, EngineComponents.EquidadeAberturaFechamento);
        }

        // Folgas em sábado/domingo (dias normais)
        var weekend = Enumerable.Range(0, _ctx.Days).Where(d => _ctx.Dates[d].DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday).ToList();
        Equity(_ctx.Employees.Where(e => e.Sectors.Length > 0).Select(e =>
        {
            var days = weekend.Where(d => !_ctx.NonWorkable(e, d) && !e.Busy.ContainsKey(d)).ToList();
            return days.Count == 0 ? null : (LinearExpr?)(days.Count - Sum(days.Select(d => (IntVar)Y[e.Index, d])));
        }), weekend.Count, _o.WeightWeekendOffEquity, "fds", EngineComponents.FinsDeSemana);
    }

    /// <summary>
    /// Rodízio de abertura/fechamento (operação com o rodízio ligado). Regras:
    /// <list type="bullet">
    /// <item>Cada turno fora do perfil custa WeightOutOfProfileShift (Flexível abrindo/fechando; Abertura/Fechamento num
    /// intermediário — só em setor sem nenhum Flexível, para cobrir almoços: PlanningContext.RotationAllows) ou
    /// WeightOutOfProfileCrossShift (Abertura
    /// fechando ou Fechamento abrindo). Mais WeightPeakDeficit por faixa de reforço de pico que o turno cobre: o reforço
    /// de pico (preferencial) nunca compensa sair do perfil — o motor só usa o rodízio quando o perfil não basta para o
    /// mínimo rígido (ou quando ele não muda o custo).</item>
    /// <item>Equidade: (máximo − mínimo) das aberturas e dos fechamentos fora do perfil (WeightRotationEquity) entre os
    /// elegíveis do setor: os Flexível que podem pegar o turno; sem Flexível, todos os de outro perfil.</item>
    /// <item>Disponibilidade, ausências, interjornada, dias seguidos, folgas, domingos etc. continuam rígidos (o turno
    /// fora do perfil passa pelas mesmas restrições).</item>
    /// </list>
    /// </summary>
    private void AddRotation()
    {
        if (_outOfProfile.Count == 0) return;
        // Dica para a busca: começar pelo perfil (o rodízio só entra onde ele não basta)
        if (_stage == SolveStage.Generation)
            foreach (var v in _outOfProfile) Hint(v.X, 0L);
        var peaks = _ctx.Slots.Where(s => s.Required > s.Minimum).GroupBy(s => (s.Day, s.Sector)).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var (e, d, t, s, x) in _outOfProfile)
        {
            var shift = _ctx.Shifts[t];
            var cross = _ctx.Employees[e].Profile is JourneyProfiles.Abertura or JourneyProfiles.Fechamento
                        && shift.Kind is JourneyProfiles.Abertura or JourneyProfiles.Fechamento;
            var weight = (long)(cross ? _o.WeightOutOfProfileCrossShift : _o.WeightOutOfProfileShift);
            if (_ctx.OperationMode && peaks.TryGetValue((d, s), out var list))
                weight += list.Where(p => PlanningContext.Covers(shift, p)).Sum(p => (long)PeakWeight(p));
            if (weight > 0) Term(EngineComponents.ForaDoPerfil, (LinearExpr)x, weight);
        }
        foreach (var (kind, name) in new[] { (JourneyProfiles.Abertura, "abertura"), (JourneyProfiles.Fechamento, "fechamento") })
        {
            var byEmployee = _outOfProfile.Where(v => _ctx.Shifts[v.T].Kind == kind).GroupBy(v => v.E)
                .ToDictionary(g => g.Key, g => g.Select(v => (IntVar)v.X).ToList());
            if (byEmployee.Count == 0) continue;
            var flexible = byEmployee.Keys.Where(k => _ctx.Employees[k].Profile == JourneyProfiles.Flexivel).ToList();
            var group = flexible.Count > 0 ? flexible : byEmployee.Keys.ToList();
            var counts = group.Select(k => (LinearExpr?)Sum(byEmployee[k])).ToList();
            _indicators[$"rodizio_{name}"] = counts.OfType<LinearExpr>().ToList();
            Equity(counts, _ctx.Days, _o.WeightRotationEquity, $"rod_{name}", EngineComponents.EquidadeRodizio);
        }
    }

    private void Equity(IEnumerable<LinearExpr?> counts, int upper, int weight, string name, string component)
    {
        var list = counts.OfType<LinearExpr>().ToList();
        if (weight <= 0 || list.Count < 2) return;
        var max = Model.NewIntVar(0, upper, $"eq_max_{name}");
        var min = Model.NewIntVar(0, upper, $"eq_min_{name}");
        foreach (var c in list)
        {
            Model.Add((LinearExpr)max >= c);
            Model.Add((LinearExpr)min <= c);
        }
        Term(component, (LinearExpr)max, weight);
        Term(component, (LinearExpr)min, -weight);
    }

    /// <summary>
    /// Folgas intercaladas (preferência geral): cada par de folgas de descanso em dias seguidos — inclusive na virada de
    /// semana e na emenda com as folgas registradas na escala anterior/seguinte — custa WeightConsecutiveDaysOffPenalty
    /// (peso negativo = bônus ao par, comportamento antigo; 0 = indiferente). Ausência/feriado sem autorização não são
    /// folga (<see cref="RestCandidate"/>). Com folgas_consecutivas_5x2 ativa o par exigido custa o mesmo em toda escala
    /// viável da semana (não muda a escolha); a penalidade só desestimula pares além dele. Para quem tem prioridade de
    /// folga dupla (<see cref="AddDoubleDayOffPriority"/>) o custo do não atendimento supera esta penalidade: um par no mês
    /// sai mais barato. Folgas nos dias preferidos do colaborador.
    /// </summary>
    private void AddDayOffPreferences()
    {
        var weight = _o.WeightConsecutiveDaysOffPenalty;
        foreach (var emp in _ctx.Employees)
        {
            for (var d = -1; d < _ctx.Days && weight != 0 && emp.Sectors.Length > 0; d++)
            {
                var a = RestCandidate(emp, d);
                var b = RestCandidate(emp, d + 1);
                if (!a.Ok || !b.Ok || (a.Y is null && b.Y is null)) continue; // não é par de folgas possível, ou é constante
                var days = new[] { a.Y, b.Y }.OfType<BoolVar>().ToList();
                var both = Model.NewBoolVar($"off2_{emp.Index}_{d}");
                if (weight > 0)
                    Model.Add((LinearExpr)both + Sum(days) >= 1); // as duas folgas ⇒ par
                else
                    Model.AddBoolAnd(days.Select(y => y.Not()).ToArray()).OnlyEnforceIf(both); // par ⇒ as duas folgas
                Term(EngineComponents.FolgasSeguidas, (LinearExpr)both, weight);
            }
            var preferred = emp.Preferences?.FolgasPreferidas;
            if (preferred is not { Count: > 0 } || _o.WeightPreferenceDayOff <= 0) continue;
            for (var d = 0; d < _ctx.Days; d++)
                if (preferred.Contains(_ctx.Dates[d].DayOfWeek) && _start[emp.Index, d] is not null)
                    Term(EngineComponents.Preferencias, (LinearExpr)Y[emp.Index, d], _o.WeightPreferenceDayOff);
        }
    }

    /// <summary>
    /// Prioridade de folga dupla (só na geração): quem tem N &gt; 0 meses seguidos sem folga dupla na fotografia
    /// (<see cref="PlanningSnapshot.FolgaDupla"/>) e fica sem nenhuma folga dupla no mês paga
    /// WeightDoubleDayOffPriority × min(N, DoubleDayOffPriorityMaxMonths). Folga dupla = dois dias seguidos de folga de
    /// descanso (<see cref="RestCandidate"/>: ausência e feriado sem autorização não contam) com os DOIS dias no período da
    /// escala — a que cruza a virada do mês não conta (<see cref="FolgaDuplaHistory"/>). O peso supera a penalidade do par
    /// de folgas seguidas (folgas intercaladas): para o priorizado a folga dupla vence a preferência geral; as regras
    /// rígidas (max_folgas_seguidas, folgas da semana, dias seguidos) e o mínimo de cobertura continuam restrições duras.
    /// Não se aplica a quem está em setor de 2 pessoas com folgas fixas e intercaladas (<see cref="AddFixedDaysOff"/>).
    /// </summary>
    private void AddDoubleDayOffPriority()
    {
        if (_stage != SolveStage.Generation || _o.WeightDoubleDayOffPriority <= 0 || _ctx.Snapshot.FolgaDupla is not { Count: > 0 } scores) return;
        var months = scores.GroupBy(s => s.EmployeeId).ToDictionary(g => g.Key, g => g.First().MesesSemFolgaDupla);
        var cap = Math.Max(1, _o.DoubleDayOffPriorityMaxMonths);
        foreach (var emp in _ctx.Employees)
        {
            var n = Math.Min(months.GetValueOrDefault(emp.Snapshot.EmployeeId), cap);
            if (n <= 0 || emp.Sectors.Length == 0) continue;
            if (FixedPatterns.ContainsKey(emp.Index)) continue; // setor de 2 pessoas: folgas fixas e intercaladas, sem folga dupla
            var pairs = new List<IntVar>();
            var certain = false;
            for (var d = _ctx.FirstDay; d < _ctx.LastDay && !certain; d++)
            {
                var a = RestCandidate(emp, d);
                var b = RestCandidate(emp, d + 1);
                if (!a.Ok || !b.Ok) continue;
                if (a.Y is null && b.Y is null) { certain = true; continue; } // dois dias sem turno possível: já é folga dupla
                var both = Model.NewBoolVar($"fd_{emp.Index}_{d}");
                foreach (var y in new[] { a.Y, b.Y }.OfType<BoolVar>()) Model.Add((LinearExpr)both + y <= 1); // par ⇒ as duas folgas
                pairs.Add(both);
            }
            if (certain || pairs.Count == 0) continue; // nada a decidir (já tem, ou não há dois dias livres seguidos no mês)
            var got = Model.NewBoolVar($"fd_{emp.Index}");
            Model.Add((LinearExpr)got <= Sum(pairs));
            Penalty(EngineComponents.FolgaDuplaPrioridade, (LinearExpr)got, (long)n * _o.WeightDoubleDayOffPriority);
        }
    }

    /// <summary>Mudança de horário: |entrada de amanhã − entrada de hoje| entre dias trabalhados seguidos (por minuto).</summary>
    private void AddStartChanges()
    {
        if (_o.WeightStartChangePerMinute <= 0) return;
        foreach (var emp in _ctx.Employees)
        {
            for (var d = 0; d < _ctx.Days - 1; d++)
            {
                if (_start[emp.Index, d] is not { } s0 || _start[emp.Index, d + 1] is not { } s1) continue;
                var starts0 = _ctx.Shifts.Where(t => _byShift.ContainsKey((emp.Index, d, t.Index))).Select(t => t.StartMin).Distinct().ToList();
                var starts1 = _ctx.Shifts.Where(t => _byShift.ContainsKey((emp.Index, d + 1, t.Index))).Select(t => t.StartMin).Distinct().ToList();
                if (starts0.Count <= 1 && starts1.Count <= 1 && starts0.SequenceEqual(starts1)) continue;
                var dev = Model.NewIntVar(0, PlanningContext.MinutesPerDay, $"dv_{emp.Index}_{d}");
                ILiteral[] both = [Y[emp.Index, d], Y[emp.Index, d + 1]];
                Model.Add((LinearExpr)dev >= (LinearExpr)s1 - (LinearExpr)s0).OnlyEnforceIf(both);
                Model.Add((LinearExpr)dev >= (LinearExpr)s0 - (LinearExpr)s1).OnlyEnforceIf(both);
                Term(EngineComponents.MudancaHorario, (LinearExpr)dev, _o.WeightStartChangePerMinute);
            }
        }
    }

    /// <summary>Preferência de período (Cedo/Tarde): penaliza cada 15 min de distância da entrada mais cedo/mais tarde possível.</summary>
    private void AddShiftPreferences()
    {
        if (_o.WeightPreferencePeriodPer15Min <= 0) return;
        foreach (var emp in _ctx.Employees)
        {
            var period = emp.Preferences?.PeriodoPreferido;
            if (period is not (JourneyPreferences.Cedo or JourneyPreferences.Tarde)) continue;
            var mine = X.Where(kv => kv.Key.E == emp.Index).ToList();
            if (mine.Count == 0) continue;
            var earliest = mine.Min(kv => _ctx.Shifts[kv.Key.T].StartMin);
            var latest = mine.Max(kv => _ctx.Shifts[kv.Key.T].StartMin);
            foreach (var (key, x) in mine)
            {
                var start = _ctx.Shifts[key.T].StartMin;
                var distance = period == JourneyPreferences.Cedo ? start - earliest : latest - start;
                if (distance > 0) Term(EngineComponents.Preferencias, (LinearExpr)x, distance / 15 * _o.WeightPreferencePeriodPer15Min);
            }
        }
    }

    // ---------------- Leitura ----------------

    /// <summary>Início do intervalo escolhido para a célula (null = não modelado no dia: o BreakPlanner posiciona depois).</summary>
    public int? BreakStartOf(Func<IntVar, long> value, int e, int d, int s) =>
        Breaks.TryGetValue((e, d, s), out var list) ? list.FirstOrDefault(b => value(b.Var) == 1)?.Start : null;
}

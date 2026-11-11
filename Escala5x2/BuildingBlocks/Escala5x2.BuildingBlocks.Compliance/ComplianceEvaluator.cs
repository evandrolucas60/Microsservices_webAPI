using Escala5x2.BuildingBlocks.Contracts.Compliance;
using Escala5x2.BuildingBlocks.Contracts.Planning;
using Escala5x2.BuildingBlocks.Contracts.Scheduling;

namespace Escala5x2.BuildingBlocks.Compliance;

/// <summary>
/// Avaliador de conformidade (evolução do ScheduleValidator): confere a escala contra as regras ATIVAS da
/// fotografia, escrito sem reaproveitar o modelo do motor. Usado pelo Worker (conferência do resultado e
/// sugestões) e pelo Scheduling.Api (avaliação gravada a cada geração/edição).
///
/// Percentual = verificações conformes / verificações totais × 100. Unidades de verificação por regra:
/// <list type="bullet">
/// <item>carga_horaria_semanal: colaborador com controle de jornada × semana civil.</item>
/// <item>Regime da semana (PlanningContext.Regime): 6x1 se a carga semanal vigente ≥ carga_minima_regime_6x1_horas,
/// senão 5x2 (o limiar não tem verificação própria; o regime de cada semana sai em RegimePorSemana).</item>
/// <item>folgas_semana_6x1 / folgas_semana_5x2: colaborador × semana civil do regime — número EXATO de folgas (nunca
/// abaixo de folgas_minimas_semana). Folga = dia sem trabalho (no setor ou em outro) que não é feriado sem autorização
/// nem ausência; esses dias reduzem os dias de trabalho.</item>
/// <item>folgas_minimas_semana (piso legal do DSR): colaborador × semana civil, contado como no regime.</item>
/// <item>Fotografias antigas, sem o modelo de regime: folgas_maximas_semana (5x2 estrito) e dias_consecutivos_max.</item>
/// <item>limite_diario_minutos / limite_diario_com_acordo_minutos / hora_extra_max_diaria_minutos: colaborador com controle de jornada × dia trabalhado no setor (com acordo de prorrogação a verificação conta para hora_extra).</item>
/// <item>limite_domingo_minutos: colaborador com controle de jornada × domingo trabalhado no setor.</item>
/// <item>dias_consecutivos_max_6x1 / _5x2: colaborador × dia trabalhado no setor (a sequência que termina nele, inclusive
/// dias da semana anterior de outro regime e do histórico, não passa do máximo do regime da semana do dia).</item>
/// <item>interjornada_minima_minutos: colaborador com controle de jornada × par de dias seguidos trabalhados (ao menos um no setor).</item>
/// <item>folga_isolada_horas_livres: colaborador × folga isolada (trabalho, folga, trabalho).</item>
/// <item>domingo_folga_janela_semanas / domingo_quinzenal_janela_semanas: colaborador (quinzenal: só quem tem a regra) × janela de N domingos que termina no horizonte.</item>
/// <item>Regras de intervalo (ver <see cref="IntervalPolicy"/>): POR CÉLULA de trabalho, com a duração efetiva da célula
/// (a dela ou a do turno) e, quando o início do intervalo está posicionado, também início/fim/trecho contínuo.
/// obrigatório/mínimo/padrão: célula com jornada líquida acima de intervalo_obrigatorio_acima_minutos; 4h a 6h: célula
/// com jornada nessa faixa; máximo: célula com intervalo; posição: célula com intervalo posicionado.</item>
/// <item>cobertura_faixa_minutos (sem operação): faixa (setor × dia × faixa) com demanda; quem está em intervalo em qualquer
/// parte da faixa não conta como presente (células sem início de intervalo: presentes o turno inteiro, como nas escalas
/// antigas).</item>
/// <item>Com operação do setor (CoverageSlot.Minimum): cobertura_minima / minimo_abertura / minimo_fechamento (RÍGIDAS,
/// severidade média): faixa do expediente × dia; reforco_demanda (PREFERENCIAL): faixa com demanda acima do mínimo.
/// No setor de 2 pessoas e no setor com quadro insuficiente gravado na fotografia (PlanningContext.SoftCoverage) o mínimo
/// é META, como no motor: a faixa descoberta é aviso (severidade baixa, "cobertura como meta"), não violação rígida.</item>
/// <item>dia_aberto_sem_ninguem (estrutural): setor × dia aberto com faixa de mínimo em que alguém podia trabalhar —
/// alguém trabalha (rígida; aviso com a cobertura como meta).</item>
/// <item>disponibilidade (estrutural, rígida): célula de trabalho fora da disponibilidade semanal do colaborador.</item>
/// <item>perfil_jornada (informativa, baixa): célula de trabalho com turno de outro tipo que o perfil (só células manuais
/// podem ficar assim: o motor só usa turnos do perfil). Com o rodízio de abertura/fechamento ligado na operação, turno da
/// operação fora do perfil não é violação (o motor o usa quando falta gente no perfil); turno fora da operação continua.</item>
/// <item>folgas_consecutivas_5x2: colaborador × semana 5x2 com ao menos 2 folgas: duas folgas em dias seguidos.</item>
/// <item>folgas_fixas (informativa, baixa): setor de 2 pessoas com folgas fixas e intercaladas × dia aberto em que os dois
/// podem trabalhar: alguém trabalha (as folgas não coincidem).</item>
/// <item>max_folgas_seguidas: colaborador × sequência de folgas de descanso com dia no horizonte (inclusive a virada de
/// semana e as folgas registradas na escala anterior/seguinte): no máximo N seguidas. Feriado sem autorização e ausência
/// não são folga e interrompem a sequência.</item>
/// <item>Continuidade: dias da escala seguinte (publicada/aprovada) entram nas sequências de dias seguidos, interjornada,
/// folga isolada e janelas de domingo que tocam o horizonte (como o histórico da semana anterior).</item>
/// <item>Jornada diária/semanal, domingo e interjornada usam os minutos pagos da célula (duração − intervalo efetivo:
/// o intervalo não é computado na jornada, CLT art. 71, §2º).</item>
/// <item>feriado_trabalho_exige_autorizacao: colaborador × feriado sem autorização no horizonte.</item>
/// <item>indisponibilidade (estrutural): colaborador × dia de férias/afastamento no horizonte.</item>
/// <item>conflito_outro_setor (estrutural): colaborador × dia já alocado em escala de outro setor.</item>
/// </list>
/// Regra bloqueada não conta verificações e aparece como "bloqueada".
/// </summary>
public sealed class ComplianceEvaluator
{
    public const string Indisponibilidade = "indisponibilidade";
    public const string ConflitoOutroSetor = "conflito_outro_setor";
    /// <summary>Estrutural: ninguém trabalha no dia em que a loja está fechada (horário de funcionamento ou feriado fechado).</summary>
    public const string LojaFechada = "loja_fechada";
    /// <summary>
    /// Informativa (baixa): setor de 2 pessoas com folgas fixas e intercaladas (PlanningContext.FixedDaysOffPairs) — em dia
    /// com a loja aberta em que os dois podem trabalhar, alguém trabalha (as folgas dos dois não coincidem).
    /// </summary>
    public const string FolgasFixas = "folgas_fixas";
    /// <summary>
    /// Estrutural: dia do período com a loja aberta e cobertura exigida no setor (faixa com mínimo) em que alguém do setor
    /// podia trabalhar e NINGUÉM está escalado. Rígida (alta) com a cobertura rígida; com a cobertura como meta (setor de 2
    /// pessoas, quadro insuficiente) é aviso (baixa) — mas sempre aparece: dia aberto sem ninguém nunca fica "100%".
    /// </summary>
    public const string DiaSemNinguem = "dia_aberto_sem_ninguem";

    private static readonly Dictionary<string, string> Units = new()
    {
        [RuleKeys.CargaHorariaSemanal] = "colaborador × semana",
        [RuleKeys.FolgasMinimasSemana] = "colaborador × semana",
        [RuleKeys.FolgasMaximasSemana] = "colaborador × semana",
        [RuleKeys.FolgasSemana6x1] = "colaborador × semana 6x1",
        [RuleKeys.FolgasSemana5x2] = "colaborador × semana 5x2",
        [RuleKeys.DiasConsecutivosMax6x1] = "colaborador × dia trabalhado em semana 6x1",
        [RuleKeys.DiasConsecutivosMax5x2] = "colaborador × dia trabalhado em semana 5x2",
        [RuleKeys.CargaMinimaRegime6x1] = "parâmetro: escolhe o regime de cada semana (sem verificação própria)",
        [RuleKeys.FolgaExtraDomingo6x1] = "parâmetro: permite 1 folga a mais na semana 6x1 com folga no domingo (verificada em folgas_semana_6x1)",
        [RuleKeys.LimiteDiario] = "colaborador × dia trabalhado",
        [RuleKeys.LimiteDiarioComAcordo] = "colaborador × dia trabalhado",
        [RuleKeys.CompensacaoJornada5x2] = "colaborador × dia trabalhado em semana 5x2 (limite diário com a compensação: carga semanal ÷ dias de trabalho)",
        [RuleKeys.HoraExtraMaxDiaria] = "colaborador com acordo × dia trabalhado",
        [RuleKeys.LimiteDomingo] = "colaborador × domingo trabalhado",
        [RuleKeys.DiasConsecutivosMax] = "colaborador × dia trabalhado",
        [RuleKeys.InterjornadaMinima] = "colaborador × par de dias trabalhados",
        [RuleKeys.FolgaIsoladaHorasLivres] = "colaborador × folga isolada",
        [RuleKeys.DomingoFolgaJanela] = "colaborador × janela de domingos",
        [RuleKeys.DomingoQuinzenalJanela] = "colaborador quinzenal × janela de domingos",
        [RuleKeys.IntervaloObrigatorioAcima] = "célula com jornada acima do limite",
        [RuleKeys.IntervaloMinimo] = "célula com jornada acima do limite e intervalo",
        [RuleKeys.IntervaloPadrao] = "célula com jornada acima do limite e intervalo",
        [RuleKeys.IntervaloMaximo] = "célula com intervalo",
        [RuleKeys.IntervaloJornada4a6h] = "célula com jornada de 4h a 6h",
        [RuleKeys.IntervaloInicioMinimoAposEntrada] = "célula com intervalo posicionado",
        [RuleKeys.IntervaloFimMinimoAntesSaida] = "célula com intervalo posicionado",
        [RuleKeys.JornadaContinuaMaxima] = "célula com intervalo posicionado (ou jornada de 4h a 6h sem intervalo)",
        [RuleKeys.CoberturaFaixa] = "faixa de horário com demanda",
        [RuleKeys.FeriadoExigeAutorizacao] = "colaborador × feriado sem autorização",
        [RuleKeys.FolgasConsecutivas5x2] = "colaborador × semana 5x2 com 2 folgas",
        [RuleKeys.MaxFolgasSeguidas] = "colaborador × sequência de folgas (com a emenda da escala anterior/seguinte)",
        [RuleKeys.CoberturaMinima] = "faixa do expediente × dia",
        [RuleKeys.MinimoAbertura] = "dia (primeira faixa do expediente)",
        [RuleKeys.MinimoFechamento] = "dia (última faixa do expediente)",
        [RuleKeys.ReforcoDemanda] = "faixa com demanda acima do mínimo",
        [RuleKeys.Disponibilidade] = "célula de trabalho × disponibilidade semanal",
        [RuleKeys.PerfilJornada] = "célula de trabalho × perfil de jornada",
        [Indisponibilidade] = "colaborador × dia indisponível",
        [ConflitoOutroSetor] = "colaborador × dia em outro setor",
        [LojaFechada] = "colaborador × dia com a loja fechada",
        [FolgasFixas] = "dia aberto em setor de 2 pessoas (folgas intercaladas)",
        [DiaSemNinguem] = "setor × dia aberto com cobertura exigida",
    };

    /// <summary>Verificações que não são linhas do cadastro de regras: descrição, categoria e aplicação.</summary>
    private static readonly Dictionary<string, (string Descricao, string Categoria, string? Aplicacao)> Structural = new()
    {
        [Indisponibilidade] = ("Não escalar em férias, afastamento ou indisponibilidade", "estrutura", null),
        [ConflitoOutroSetor] = ("Um setor por dia (pessoa já escalada em outro setor)", "estrutura", null),
        [LojaFechada] = ("Não escalar com a loja fechada (horário de funcionamento ou feriado fechado)", "estrutura", null),
        [FolgasFixas] = ("Folgas intercaladas no setor de 2 pessoas (as folgas dos dois não coincidem)", "estrutura", nameof(RuleApplication.Preferencial)),
        [DiaSemNinguem] = ("Dia com a loja aberta e ninguém escalado no setor", "cobertura", nameof(RuleApplication.Rigida)),
        [RuleKeys.CoberturaMinima] = ("Cobertura mínima do expediente (presentes fora do intervalo)", "cobertura", nameof(RuleApplication.Rigida)),
        [RuleKeys.MinimoAbertura] = ("Mínimo de pessoas na abertura", "cobertura", nameof(RuleApplication.Rigida)),
        [RuleKeys.MinimoFechamento] = ("Mínimo de pessoas no fechamento", "cobertura", nameof(RuleApplication.Rigida)),
        [RuleKeys.ReforcoDemanda] = ("Reforço nos horários de maior demanda (acima do mínimo)", "cobertura", nameof(RuleApplication.Preferencial)),
        [RuleKeys.Disponibilidade] = ("Disponibilidade semanal do colaborador", "estrutura", nameof(RuleApplication.Rigida)),
        [RuleKeys.PerfilJornada] = ("Turno do perfil de jornada (abertura, fechamento ou flexível)", "estrutura", nameof(RuleApplication.Preferencial)),
    };

    private static readonly HashSet<string> CoverageKeys =
        [RuleKeys.CoberturaFaixa, RuleKeys.CoberturaMinima, RuleKeys.MinimoAbertura, RuleKeys.MinimoFechamento];

    private sealed class Tally
    {
        public int Checks;
        public int Violations;
    }

    private sealed class Run(PlanningContext ctx)
    {
        public readonly Dictionary<string, Tally> Tallies = new(StringComparer.Ordinal);
        public readonly List<ComplianceViolationDto> Violations = [];
        /// <summary>Horas e dias por colaborador × semana (informativo).</summary>
        public readonly List<WeeklyHoursDto> Hours = [];
        private readonly HashSet<string> _ids = [];

        public void Check(string key, bool ok, bool rigid, Func<(EmployeeInfo? Emp, DateOnly[] Dates, string Detail)> violation)
        {
            if (!Tallies.TryGetValue(key, out var t)) Tallies[key] = t = new Tally();
            t.Checks++;
            if (ok) return;
            t.Violations++;
            var (emp, all, detail) = violation();
            // Datas apontadas: só as do período (dias de borda/da escala vizinha não estão na grade desta escala)
            var inPeriod = all.Where(x => x >= ctx.PeriodStart && x <= ctx.PeriodEnd).ToArray();
            var dates = inPeriod.Length > 0 ? inPeriod : all;
            var severity = !rigid ? "baixa" : CoverageKeys.Contains(key) ? "media" : "alta";
            var baseId = $"{key}:{emp?.Snapshot.EmployeeId.ToString("N")[..8] ?? "geral"}:{(dates.Length > 0 ? dates[^1].ToString("yyyyMMdd") : "0")}";
            var id = baseId;
            for (var n = 2; !_ids.Add(id); n++) id = $"{baseId}:{n}";
            Violations.Add(new ComplianceViolationDto(id, key, Description(ctx, key), severity,
                emp?.Snapshot.EmployeeId, emp?.Snapshot.Name, dates, detail));
        }
    }

    private static string Description(PlanningContext ctx, string key) =>
        Structural.TryGetValue(key, out var s) ? s.Descricao : ctx.Rules.Representative(key)?.Descricao ?? key;

    private static string H(int minutes) => minutes % 60 == 0 ? $"{minutes / 60}h" : $"{minutes / 60}h{minutes % 60:00}";

    public ComplianceResult Evaluate(PlanningContext ctx, IReadOnlyList<AssignmentDto> cells)
    {
        var run = new Run(ctx);
        var byEmployee = cells.GroupBy(a => a.EmployeeId).ToDictionary(g => g.Key, g => g.ToList());

        foreach (var emp in ctx.Employees)
            EvaluateEmployee(ctx, run, emp, byEmployee.GetValueOrDefault(emp.Snapshot.EmployeeId) ?? []);

        EvaluateIntervals(ctx, run, cells);
        EvaluateCoverage(ctx, run, cells);
        EvaluateOpenDays(ctx, run, cells);
        EvaluateFixedDaysOff(ctx, run, cells);

        return Build(ctx, run);
    }

    /// <summary>
    /// <see cref="DiaSemNinguem"/>: por setor e dia do período com a loja aberta e faixa com mínimo (cobertura exigida), em
    /// que algum colaborador do setor podia trabalhar (sem ausência, outro setor, feriado sem autorização nem dia
    /// indisponível na disponibilidade semanal), alguém trabalha no setor. Rígida com a cobertura rígida; aviso (baixa) com a
    /// cobertura como meta.
    /// </summary>
    private static void EvaluateOpenDays(PlanningContext ctx, Run run, IReadOnlyList<AssignmentDto> cells)
    {
        var worked = cells.Select(c => (c.SectorId, c.Date)).ToHashSet();
        foreach (var day in ctx.Slots.Where(s => s.Minimum > 0 && ctx.OpenWorkDay(s.Day)).GroupBy(s => (s.Sector, s.Day)).OrderBy(g => g.Key.Day))
        {
            var (sector, d) = day.Key;
            var date = ctx.Dates[d];
            var someoneCould = ctx.Employees.Any(e => e.Sectors.Contains(sector) && !e.Busy.ContainsKey(d) && !ctx.NonWorkable(e, d)
                                                      && !(e.Availability.TryGetValue(date.DayOfWeek, out var w) && !w.Disponivel));
            if (!someoneCould) continue;
            var hard = day.Any(ctx.HardCoverage);
            var name = ctx.Sectors[sector].Name;
            run.Check(DiaSemNinguem, worked.Contains((ctx.Sectors[sector].SectorId, date)), hard,
                () => (null, [date], $"{name} em {date:dd/MM}: a loja está aberta ({ctx.DayWindowText(d)}) e ninguém está escalado no setor" +
                                     (hard ? "." : " (cobertura como meta: confira o quadro e a configuração da loja/operação).")));
        }
    }

    private static void EvaluateEmployee(PlanningContext ctx, Run run, EmployeeInfo emp, List<AssignmentDto> mine)
    {
        var name = emp.Snapshot.Name;
        var controlled = emp.Snapshot.JourneyControlled;
        // Trabalho no setor da escala (uma célula por dia: chave única no banco; só os dias do período)
        var here = new Dictionary<int, AssignmentDto>();
        foreach (var a in mine.OrderBy(a => a.Date))
        {
            var d = ctx.DayIndex(a.Date);
            if (ctx.InPeriod(d)) here.TryAdd(d, a);
        }
        // Dia de borda sem escala vizinha: nem folga nem trabalho conhecidos
        bool Unknown(int d) => emp.EdgeUnknown.Contains(d);

        // Trabalho em qualquer lugar (setor, outro setor, histórico publicado): (início, fim absoluto, minutos pagos)
        (int Start, int EndAbs, int Paid)? WorkAt(int d)
        {
            if (d >= 0 && d < ctx.Days)
            {
                if (here.TryGetValue(d, out var a))
                {
                    var s = PlanningContext.ToMinutes(a.Start);
                    return (s, PlanningContext.EndAbs(s, PlanningContext.ToMinutes(a.End)), a.PaidMinutes);
                }
                if (emp.Busy.TryGetValue(d, out var b)) return (b.Start, b.EndAbs, b.PaidMinutes);
                return null;
            }
            if (d >= ctx.Days) return ctx.FollowingAt(emp, d) is { } f ? (f.Start, f.EndAbs, 0) : null;
            return emp.History.TryGetValue(ctx.Start.AddDays(d), out var h) ? (h.Start, h.EndAbs, 0) : null;
        }
        bool Worked(int d) => WorkAt(d) is not null;
        DateOnly Date(int d) => ctx.Start.AddDays(d);

        // Estruturais: indisponibilidade e conflito com outro setor (dias do período)
        for (var d = ctx.FirstDay; d <= ctx.LastDay; d++)
        {
            var day = d;
            if (!ctx.IsAvailable(emp, d))
                run.Check(Indisponibilidade, !here.ContainsKey(d), true,
                    () => (emp, [Date(day)], $"{name} está escalado(a) em {Date(day):dd/MM}, dia de férias/afastamento/indisponibilidade."));
            if (emp.Busy.TryGetValue(d, out var busy))
                run.Check(ConflitoOutroSetor, !here.ContainsKey(d), true,
                    () => (emp, [Date(day)], $"{name} já está escalado(a) em {busy.SectorName} em {Date(day):dd/MM}."));
            if (ctx.HolidayRule(d) is { } holiday)
                run.Check(holiday.Key, !here.ContainsKey(d), holiday.Rigid,
                    () => (emp, [Date(day)], $"{name} está escalado(a) no feriado {Date(day):dd/MM} sem autorização em convenção coletiva."));
            if (ctx.IsClosed(d))
                run.Check(LojaFechada, !here.ContainsKey(d), true,
                    () => (emp, [Date(day)], $"{name} está escalado(a) em {Date(day):dd/MM}, dia em que a loja está fechada."));
        }

        // Limite diário / hora extra e limite de domingo (por dia trabalhado no setor)
        foreach (var (d, a) in here)
        {
            var day = d;
            // Disponibilidade semanal (rígida) e perfil de jornada (informativo: só células manuais ficam fora do perfil)
            var shift = ctx.ShiftById(a.ShiftId) ?? new ShiftInfo(-1, new ShiftSnapshot(a.ShiftId, "?", a.Start, a.End, 0),
                PlanningContext.ToMinutes(a.Start), PlanningContext.EndAbs(PlanningContext.ToMinutes(a.Start), PlanningContext.ToMinutes(a.End)), a.PaidMinutes);
            if (emp.Availability.ContainsKey(ctx.Dates[d].DayOfWeek))
                run.Check(RuleKeys.Disponibilidade, ctx.AvailableAt(emp, d, shift), true,
                    () => (emp, [Date(day)], $"{name} está escalado(a) em {Date(day):dd/MM} ({a.Start:HH\\:mm}–{a.End:HH\\:mm}) fora da disponibilidade do dia ({ctx.AvailabilityText(emp, day)})."));
            // Rodízio de abertura/fechamento ligado: turno da operação fora do perfil é escolha do motor (informativo, a
            // grade marca "R"), não violação; turno fora da operação continua apontado
            if (ctx.OperationMode && emp.Profile is { } profile)
                run.Check(RuleKeys.PerfilJornada, shift.Kind == profile || (ctx.Rotation && shift.Kind is not null), false,
                    () => (emp, [Date(day)], $"{name} (perfil {JourneyProfiles.Rotulo(profile)}) está em {a.Start:HH\\:mm}–{a.End:HH\\:mm} em {Date(day):dd/MM}, " +
                                             $"turno {(shift.Kind is null ? "fora da operação" : $"de {JourneyProfiles.Rotulo(shift.Kind).ToLowerInvariant()}")}."));
            if (ctx.AllowedDaily(emp, d) is { } allowed)
                run.Check(allowed.Key, a.PaidMinutes <= allowed.Value, allowed.Rigid,
                    () => (emp, [Date(day)], $"{name} tem {H(a.PaidMinutes)} de jornada em {Date(day):dd/MM} (máximo {H(allowed.Value)})."));
            if (controlled && ctx.IsSunday(d) && ctx.SundayLimit(d) is { } sunday)
                run.Check(sunday.Key, a.PaidMinutes <= sunday.Value, sunday.Rigid,
                    () => (emp, [Date(day)], $"{name} tem {H(a.PaidMinutes)} de jornada no domingo {Date(day):dd/MM} (máximo {H(sunday.Value)})."));
        }

        // Semana civil: carga horária e folgas mínimas
        for (var w = 0; w < ctx.Weeks; w++)
        {
            var week = w;
            var weekDays = Enumerable.Range(w * 7, 7).ToList();
            var first = Date(w * 7);
            var workedDays = weekDays.Count(Worked);
            // Folga = sem trabalho e não é feriado sem autorização nem ausência (estes não viram folga no 5x2 estrito).
            // Semana parcial nas pontas do mês: os dias de borda contam como a escala vizinha registrou; sem escala vizinha
            // (unknown) o dia pode ser folga ou trabalho — as contas aceitam qualquer um dos dois.
            var offDays = weekDays.Where(d => !Worked(d) && !ctx.NonWorkable(emp, d) && !Unknown(d)).ToList();
            var unknown = weekDays.Count(d => !Worked(d) && !ctx.NonWorkable(emp, d) && Unknown(d));
            var unknownNote = unknown > 0 ? $"; {unknown} dia(s) da semana no mês vizinho ainda sem escala" : "";
            var holidays = weekDays.Count(d => !Worked(d) && ctx.NonWorkable(emp, d) && ctx.IsAvailable(emp, d));
            var absences = weekDays.Count(d => !Worked(d) && !ctx.IsAvailable(emp, d) && !emp.Busy.ContainsKey(d));
            var note = holidays + absences > 0
                ? $" ({string.Join(" e ", new[] { holidays > 0 ? $"{holidays} feriado(s)" : null, absences > 0 ? $"{absences} dia(s) de ausência" : null }.OfType<string>())} não conta(m) como folga)"
                : "";
            string Folgas(int n) => n == 1 ? "folga" : "folgas";
            var regime = ctx.RegimeLabel(w);
            var normalCount = 7 - weekDays.Count(d => !Worked(d) && ctx.NonWorkable(emp, d));
            var exact = ctx.RestDays(w);
            if (ctx.UsesRegimes)
            {
                // Número exato do regime (6x1/5x2); semana com menos dias normais que o número: todos de folga
                if (emp.Sectors.Length > 0 && exact is { } ex)
                {
                    // Dias sem trabalho forçado (loja fechada, disponibilidade, sem turno possível) além do número: folgas
                    // inevitáveis, a conta é a do motor (PlanningContext.WeekRestDays)
                    var forced = weekDays.Count(d => !Worked(d) && !ctx.NonWorkable(emp, d) && ctx.ForcedOff(emp, d));
                    var expected = Math.Max(Math.Min(ex.Value, normalCount), forced);
                    // 6x1 com folga_extra_domingo_6x1: quem folga no domingo pode ter 1 folga a mais (rodízio de domingos)
                    var sunday = w * 7 + 6;
                    var sundayExtra = ctx.SundayExtraDayOff(w) is not null && (offDays.Contains(sunday) || (Unknown(sunday) && !Worked(sunday)))
                                      && expected + 1 <= normalCount;
                    var max = sundayExtra ? expected + 1 : expected;
                    // Com dias de borda indefinidos: as folgas conhecidas não passam do número e ainda dá para chegar nele
                    var ok = unknown == 0
                        ? offDays.Count == expected || (sundayExtra && offDays.Count == expected + 1)
                        : offDays.Count <= max && offDays.Count + unknown >= expected;
                    run.Check(ex.Key, ok, ex.Rigid,
                        () => (emp, (offDays.Count > expected ? offDays : weekDays.Where(Worked).ToList()).Select(Date).ToArray(),
                            $"{name} tem {offDays.Count} {Folgas(offDays.Count)} na semana de {first:dd/MM} ({regime}); exatamente {expected}" +
                            $"{(sundayExtra ? $" (ou {expected + 1} com a folga de domingo)" : "")}{note}{unknownNote}{Seam(ctx, weekDays)}."));
                }
                // folgas_consecutivas_5x2: semana 5x2 com ao menos 2 folgas → duas delas em dias seguidos (com dias de borda
                // indefinidos e folgas ainda por completar, a dupla ainda pode se formar)
                if (emp.Sectors.Length > 0 && ctx.ConsecutiveDaysOff(w) is { } pairRule && offDays.Count >= 2)
                {
                    var target = exact is { } ex2 ? Math.Min(ex2.Value, normalCount) : 2;
                    var pair = offDays.Any(d => offDays.Contains(d + 1)) || (unknown > 0 && offDays.Count < target);
                    run.Check(pairRule.Key, pair, pairRule.Rigid,
                        () => (emp, offDays.Select(Date).ToArray(),
                            $"{name} tem as folgas da semana de {first:dd/MM} em dias separados ({string.Join(", ", offDays.Select(d => Date(d).ToString("dd/MM")))}); " +
                            "a política exige 2 folgas seguidas no 5x2."));
                }
            }
            else if ((emp.Sectors.Length > 0 ? exact : null) is { } mx)
            {
                run.Check(mx.Key, offDays.Count <= mx.Value, mx.Rigid,
                    () => (emp, offDays.Select(Date).ToArray(),
                        $"{name} tem {offDays.Count} {Folgas(offDays.Count)} na semana de {first:dd/MM}; máximo {mx.Value}{note}."));
            }
            if (ctx.MinRestDays(w) is { } rest)
            {
                if (exact is not null || ctx.UsesRegimes)
                {
                    // Piso legal do DSR (folgas contadas como no regime: feriado/ausência não são folga)
                    var floor = Math.Min(rest.Value, normalCount);
                    run.Check(rest.Key, offDays.Count + unknown >= floor, rest.Rigid,
                        () => (emp, weekDays.Where(Worked).Select(Date).ToArray(),
                            $"{name} tem só {offDays.Count} {Folgas(offDays.Count)} na semana de {first:dd/MM}" +
                            $"{(regime is null ? "" : $" ({regime})")}; mínimo legal {rest.Value}{note}."));
                }
                else
                {
                    run.Check(rest.Key, workedDays <= 7 - rest.Value, rest.Rigid,
                        () => (emp, weekDays.Where(Worked).Select(Date).ToArray(),
                            $"{name} trabalha {workedDays} dias na semana de {first:dd/MM} (mínimo de {rest.Value} folga(s))."));
                }
            }
            var weekMinutes = weekDays.Sum(d => WorkAt(d)?.Paid ?? 0);
            // Semana parcial (ponta do mês): dias e minutos só do período, para o aproveitamento proporcional da carga
            var periodDays = weekDays.Where(ctx.InPeriod).ToList();
            run.Hours.Add(new WeeklyHoursDto(emp.Snapshot.EmployeeId, name, first, controlled, workedDays, offDays.Count, holidays, absences,
                weekMinutes, controlled ? ctx.WeeklyLimit(w)?.Value : null, ctx.Regime(w)?.Name,
                periodDays.Count, periodDays.Sum(d => WorkAt(d)?.Paid ?? 0)));
            if (controlled && ctx.WeeklyLimit(w) is { } limit)
            {
                var minutes = weekDays.Sum(d => WorkAt(d)?.Paid ?? 0);
                var cap = ctx.WeeklyOvertimeCap(emp, week);
                var max = cap is null ? int.MaxValue : limit.Value + cap.Value.Value;
                run.Check(limit.Key, minutes <= max, limit.Rigid,
                    () => (emp, weekDays.Where(Worked).Select(Date).ToArray(),
                        $"{name} soma {minutes / 60.0:0.#}h na semana de {first:dd/MM} (limite {limit.Value / 60.0:0.#}h" +
                        (cap is { Value: > 0 } c ? $" + até {H(c.Value)} de hora extra)." : ").")));
            }
        }

        // Continuidade com a escala seguinte: dias posteriores conhecidos (publicada/aprovada depois do período)
        // (inclui os dias de borda depois do fim do período, já da escala seguinte)
        var lastFollowing = emp.Following.Count == 0 ? ctx.Days - 1 : ctx.DayIndex(emp.Following.Keys.Max());
        var followingDays = Enumerable.Range(ctx.LastDay + 1, Math.Max(0, lastFollowing - ctx.LastDay)).Where(Worked).ToList();

        // Dias consecutivos (inclui histórico publicado, outros setores e a escala seguinte)
        foreach (var d in here.Keys.Order().Concat(followingDays))
        {
            if (ctx.MaxConsecutiveAt(d) is not { } max) continue;
            var start = d;
            while (start - 1 >= -max.Value - 30 && Worked(start - 1)) start--;
            // Dia da escala seguinte: só a sequência que vem do horizonte (com dia trabalhado no setor) é desta escala
            if (d > ctx.LastDay && !here.Keys.Any(k => k >= start)) continue;
            var length = d - start + 1;
            var day = d;
            var regime = ctx.Regime(d / 7);
            run.Check(max.Key, length <= max.Value, max.Rigid,
                () => (emp, Enumerable.Range(start, length).Where(x => x >= 0).Select(Date).ToArray(),
                    $"{name} chega ao {length}º dia seguido de trabalho em {Date(day):dd/MM} (máximo {max.Value}" +
                    $"{(regime is null ? "" : $" no regime {regime.Name}")}){Seam(ctx, Enumerable.Range(start, length))}."));
        }

        // max_folgas_seguidas: sequências de folgas de descanso que tocam o horizonte (com as folgas registradas na escala
        // anterior/seguinte). Ausência e feriado sem autorização não são folga e interrompem a sequência.
        if (emp.Sectors.Length > 0) CheckDaysOffInARow(ctx, run, emp, Worked, Date);

        // Interjornada (com controle de jornada) e folga isolada (todos); os 2 primeiros dias da escala seguinte também
        for (var d = 0; d < ctx.Days + 2; d++)
        {
            var day = d;
            var cur = WorkAt(d);
            if (cur is null) continue;

            var prev = WorkAt(d - 1);
            if (controlled && prev is not null && (here.ContainsKey(d) || here.ContainsKey(d - 1)) && ctx.InterShiftRest(d) is { } rest)
            {
                var free = PlanningContext.Gap(prev.Value.EndAbs, cur.Value.Start);
                run.Check(rest.Key, free >= rest.Value, rest.Rigid,
                    () => (emp, new[] { Date(day - 1), Date(day) }.Where(x => x >= ctx.Start).ToArray(),
                        $"{name} tem só {H(Math.Max(0, free))} entre a jornada de {Date(day - 1):dd/MM} e a de {Date(day):dd/MM} (mínimo {H(rest.Value)})" +
                        $"{Seam(ctx, [day - 1, day])}."));
            }

            var before = WorkAt(d - 2);
            // Dia de borda sem escala vizinha no meio: não se sabe se é folga
            if (prev is null && before is not null && (here.ContainsKey(d) || here.ContainsKey(d - 2)) && !Unknown(d - 1)
                && ctx.IsolatedDayOffFree(Math.Max(0, d - 1)) is { } isolated)
            {
                var free = PlanningContext.MinutesPerDay + PlanningContext.Gap(before.Value.EndAbs, cur.Value.Start);
                run.Check(isolated.Key, free >= isolated.Value, isolated.Rigid,
                    () => (emp, new[] { Date(day - 2), Date(day - 1), Date(day) }.Where(x => x >= ctx.Start).ToArray(),
                        $"{name} tem folga isolada em {Date(day - 1):dd/MM} com só {H(free)} livres (mínimo {H(isolated.Value)})" +
                        $"{Seam(ctx, [day - 2, day - 1, day])}."));
            }
        }

        // Domingos: janela geral e, para quem tem regra quinzenal, a janela quinzenal (também as janelas que terminam em
        // domingos da escala seguinte e ainda tocam o horizonte)
        var sundays = Enumerable.Range(0, ctx.Days).Where(ctx.IsSunday)
            .Concat(Enumerable.Range(ctx.Days, Math.Max(0, lastFollowing - ctx.Days + 1)).Where(d => Date(d).DayOfWeek == DayOfWeek.Sunday))
            .ToList();
        foreach (var s in sundays)
        {
            if (ctx.SundayWindow(s) is { } window) CheckSundayWindow(ctx, run, emp, s, window, Worked, Date);
            if (emp.Snapshot.SundayRule == SundayRule.Biweekly && ctx.BiweeklyWindow(s) is { } biweekly)
                CheckSundayWindow(ctx, run, emp, s, biweekly, Worked, Date);
        }
    }

    /// <summary>
    /// max_folgas_seguidas: uma verificação por sequência de folgas de descanso que tem algum dia no período, com a
    /// classificação comum ao motor (<see cref="PlanningContext.DayOffKindOf"/>; semântica em PlanningContext.DaysOff).
    /// Folga no período = sem trabalho (no setor ou em outro) num dia que pode ser folga; fora dele, só a folga registrada na
    /// escala anterior/seguinte. Ausência, feriado sem autorização e trabalho interrompem a sequência; loja fechada,
    /// indisponibilidade na disponibilidade semanal e dia sem turno possível são neutros (não contam nem interrompem).
    /// Violação: algum trecho de N + 1 folgas seguidas da sequência (N = valor da regra no último dia do trecho) toca o
    /// período — o mesmo critério do motor.
    /// </summary>
    private static void CheckDaysOffInARow(PlanningContext ctx, Run run, EmployeeInfo emp, Func<int, bool> worked, Func<int, DateOnly> date)
    {
        var (from, to) = ctx.DaysOffRange;
        var rest = new List<int>();
        var neutral = new List<int>();
        for (var d = from; d <= to + 1; d++)
        {
            var kind = d > to || worked(d) ? DayOffKind.Interrupts : ctx.DayOffKindOf(emp, d, edgeUnknownIsRest: false);
            if (kind == DayOffKind.Rest) { rest.Add(d); continue; }
            if (kind == DayOffKind.Neutral) { if (rest.Count > 0) neutral.Add(d); continue; }
            if (rest.Count > 0) CheckRestSequence(ctx, run, emp, rest, neutral.Where(n => n < rest[^1]).ToList(), date);
            rest.Clear();
            neutral.Clear();
        }
    }

    /// <summary>Uma sequência de folgas (dias neutros fora da contagem): verificação quando tem dia no período ou trecho que o toca.</summary>
    private static void CheckRestSequence(PlanningContext ctx, Run run, EmployeeInfo emp, List<int> seq, List<int> neutral, Func<int, DateOnly> date)
    {
        // Primeiro trecho de N + 1 folgas que passa do máximo tocando o período
        (int End, RuleValue Rule)? broken = null;
        RuleValue? any = null;
        for (var i = 0; i < seq.Count && broken is null; i++)
        {
            if (ctx.MaxDaysOffInARow(seq[i]) is not { } max) continue;
            if (ctx.InPeriod(seq[i])) any ??= max;
            var n = Math.Max(1, max.Value);
            if (i >= n && ctx.TouchesPeriod(seq[i - n], seq[i])) broken = (i, max);
        }
        if (broken is null && any is null) return; // regra bloqueada nos dias da sequência, ou sequência só da escala vizinha
        var used = broken?.Rule ?? any!.Value;
        var length = seq.Count;
        var first = seq[0];
        var last = seq[^1];
        var days = seq.ToList();
        var skipped = neutral.Count == 0
            ? ""
            : ", sem contar " + string.Join(", ", neutral.Select(n => $"{date(n):dd/MM} ({ctx.ForcedOffReason(emp, n)})"));
        run.Check(used.Key, broken is null, used.Rigid,
            () => (emp, days.Where(ctx.InPeriod).Select(date).ToArray(),
                $"{emp.Snapshot.Name} tem {length} folgas seguidas de {date(first):dd/MM} a {date(last):dd/MM} (máximo {used.Value}){skipped}" +
                $"{(first < ctx.FirstDay || last > ctx.LastDay ? ", contando as folgas registradas" : "")}{Seam(ctx, [first, last])}."));
    }

    /// <summary>Na janela de N domingos que termina no domingo <paramref name="sunday"/>, pelo menos um de folga.</summary>
    private static void CheckSundayWindow(PlanningContext ctx, Run run, EmployeeInfo emp, int sunday, RuleValue window, Func<int, bool> worked,
        Func<int, DateOnly> date)
    {
        var n = Math.Max(1, window.Value);
        var slice = Enumerable.Range(0, n).Select(k => sunday - 7 * (n - 1 - k)).ToList();
        if (slice[0] > ctx.LastDay) return; // janela inteira na escala seguinte: não é desta escala
        var allWorked = slice.All(worked);
        run.Check(window.Key, !allWorked, window.Rigid,
            () => (emp, slice.Where(d => d >= 0).Select(date).ToArray(),
                $"{emp.Snapshot.Name} trabalha {n} domingos seguidos até {date(sunday):dd/MM} (folga em ao menos 1 a cada {n}){Seam(ctx, slice)}."));
    }

    /// <summary>" — continuidade com a escala anterior (emenda em dd/MM)" quando a verificação usa dias fora do horizonte.</summary>
    private static string Seam(PlanningContext ctx, IEnumerable<int> days) => ctx.SeamText(days) is { } text ? $" — {text}" : "";

    /// <summary>
    /// Intervalo de refeição POR CÉLULA de trabalho (duração efetiva da célula, posição quando informada), com as
    /// regras ativas do dia (<see cref="IntervalPolicy"/>). Células sem início de intervalo (escalas antigas) só têm
    /// as verificações de duração.
    /// </summary>
    private static void EvaluateIntervals(PlanningContext ctx, Run run, IReadOnlyList<AssignmentDto> cells)
    {
        var employees = ctx.Employees.ToDictionary(e => e.Snapshot.EmployeeId);
        foreach (var a in cells.OrderBy(c => c.Date))
        {
            var d = ctx.DayIndex(a.Date);
            if (!ctx.InPeriod(d)) continue;
            var emp = employees.GetValueOrDefault(a.EmployeeId);
            var t = ctx.Times(a);
            var label = $"{emp?.Snapshot.Name ?? "Colaborador"} em {a.Date:dd/MM} ({a.Start:HH\\:mm}–{a.End:HH\\:mm})";
            foreach (var check in ctx.Interval(d).Checks(t.Start, t.EndAbs, t.BreakStart, t.BreakMinutes))
                run.Check(check.Key, check.Ok, check.Rigid, () => (emp, [a.Date], $"{label}: {check.Text}."));
        }
    }

    /// <summary>
    /// Pessoas presentes na faixa: o turno cobre a faixa inteira e a pessoa não está em intervalo em nenhum momento
    /// dela. Célula sem início de intervalo (escalas/fotografias antigas): conta como presente o turno inteiro,
    /// como antes.
    /// </summary>
    public static (int Present, int OnBreak) Presence(PlanningContext ctx, IEnumerable<AssignmentDto> cells, CoverageSlot slot)
    {
        int present = 0, onBreak = 0;
        foreach (var a in cells)
        {
            var t = ctx.Times(a);
            if (!PlanningContext.Covers(t.Start, t.EndAbs, slot)) continue;
            if (t.OnBreak(slot.SlotStart, slot.SlotStart + slot.SlotMinutes)) onBreak++;
            else present++;
        }
        return (present, onBreak);
    }

    private static void EvaluateCoverage(PlanningContext ctx, Run run, IReadOnlyList<AssignmentDto> cells)
    {
        var byDaySector = cells.GroupBy(c => (c.Date, c.SectorId)).ToDictionary(g => g.Key, g => g.ToList());
        // Só os dias do período (a faixa de um dia de borda é do mês vizinho: só orienta o motor)
        foreach (var slot in ctx.Slots.Where(s => ctx.InPeriod(s.Day)))
        {
            var date = ctx.Dates[slot.Day];
            var sector = ctx.Sectors[slot.Sector];
            var list = byDaySector.GetValueOrDefault((date, sector.SectorId)) ?? [];
            var (count, onBreak) = Presence(ctx, list, slot);
            var end = slot.SlotStart + slot.SlotMinutes;
            var faixa = $"{sector.Name} em {date:dd/MM} das {slot.SlotStart / 60:00}:{slot.SlotStart % 60:00} às {end / 60 % 24:00}:{end % 60:00}";
            var breaks = onBreak > 0 ? $" ({onBreak} em intervalo)" : "";
            // Mínimo rígido (operação: cobertura mínima/abertura/fechamento; sem operação: demanda com cobertura rígida)
            if (slot.Minimum > 0)
            {
                var label = slot.MinimumKey switch
                {
                    RuleKeys.MinimoAbertura => "mínimo na abertura",
                    RuleKeys.MinimoFechamento => "mínimo no fechamento",
                    RuleKeys.CoberturaMinima => "cobertura mínima",
                    _ => null
                };
                // Setor de 2 pessoas ou com quadro insuficiente (PlanningContext.SoftCoverage): o mínimo é meta — faixa
                // descoberta é aviso (baixa), como no motor
                var hard = ctx.HardCoverage(slot);
                run.Check(slot.MinimumKey, count >= slot.Minimum, hard,
                    () => (null, [date], $"{faixa}: {count} de {slot.Minimum} pessoa(s){breaks}{(label is null ? "" : $" — {label}")}" +
                                         $"{(hard ? "" : ctx.TwoPersonCoverage(slot.Sector)
                                             ? " (setor de 2 pessoas: cobertura como meta, não bloqueia a escala)"
                                             : " (quadro insuficiente: cobertura como meta, não bloqueia a escala)")}."));
            }
            // Acima do mínimo: preferencial (reforço de pico; sem operação, a demanda com cobertura preferencial)
            if (slot.Required > slot.Minimum)
                run.Check(slot.DemandKey, count >= slot.Required, false,
                    () => (null, [date], $"{faixa}: {count} de {slot.Required} pessoa(s) na demanda{breaks}."));
        }
    }

    /// <summary>
    /// Setor de 2 pessoas com folgas fixas e intercaladas: em cada dia do período com a loja aberta em que os dois podem
    /// trabalhar (sem ausência, feriado sem autorização, outro setor nem dia indisponível na disponibilidade semanal),
    /// pelo menos um trabalha no setor. Informativa (preferencial, baixa): aponta edição manual que faz as folgas dos dois
    /// coincidirem; as regras legais têm as próprias verificações. Escala gerada com o padrão sempre cumpre.
    /// </summary>
    private static void EvaluateFixedDaysOff(PlanningContext ctx, Run run, IReadOnlyList<AssignmentDto> cells)
    {
        if (ctx.FixedDaysOffPairs.Count == 0) return;
        var worked = cells.Select(c => (c.EmployeeId, c.Date)).ToHashSet();
        bool CanWork(EmployeeInfo e, int d) =>
            !e.Busy.ContainsKey(d) && !ctx.NonWorkable(e, d)
            && !(e.Availability.TryGetValue(ctx.Dates[d].DayOfWeek, out var w) && !w.Disponivel);
        foreach (var pair in ctx.FixedDaysOffPairs)
        {
            for (var d = ctx.FirstDay; d <= ctx.LastDay; d++)
            {
                if (ctx.IsClosed(d) || !CanWork(pair.A, d) || !CanWork(pair.B, d)) continue;
                var date = ctx.Dates[d];
                var someone = worked.Contains((pair.A.Snapshot.EmployeeId, date)) || worked.Contains((pair.B.Snapshot.EmployeeId, date));
                run.Check(FolgasFixas, someone, false,
                    () => (null, [date], $"{ctx.Sectors[pair.Sector].Name} em {date:dd/MM}: {pair.A.Snapshot.Name} e {pair.B.Snapshot.Name} de folga no mesmo dia " +
                                         "(setor de 2 pessoas: folgas fixas e intercaladas, as folgas não coincidem)."));
            }
        }
    }

    private static ComplianceResult Build(PlanningContext ctx, Run run)
    {
        static double Pct(int checks, int violations) =>
            checks == 0 ? 100 : Math.Round((checks - violations) * 100.0 / checks, 1, MidpointRounding.AwayFromZero);

        var keys = ctx.Rules.Keys.Concat([Indisponibilidade, ConflitoOutroSetor])
            .Concat(ctx.OperationMode ? RuleKeys.Operacao : [])
            .Concat(run.Tallies.Keys).Distinct().ToList();
        var porRegra = keys.Select(k =>
            {
                var t = run.Tallies.GetValueOrDefault(k) ?? new Tally();
                var rep = ctx.Rules.Representative(k);
                var meta = Structural.GetValueOrDefault(k);
                return new RuleComplianceDto(k, Description(ctx, k), rep?.Categoria ?? meta.Categoria ?? "estrutura", t.Checks, t.Violations,
                    Pct(t.Checks, t.Violations), ctx.Rules.BlockedInHorizon(k), rep?.Aplicacao.ToString() ?? meta.Aplicacao,
                    Units.GetValueOrDefault(k) ?? "não verificada pelo avaliador");
            })
            .OrderBy(r => r.Categoria == "estrutura").ThenBy(r => r.Categoria).ThenBy(r => r.Chave)
            .ToList();

        var total = run.Tallies.Values.Sum(t => t.Checks);
        var violations = run.Tallies.Values.Sum(t => t.Violations);
        var ordered = run.Violations
            .OrderBy(v => v.Severidade switch { "alta" => 0, "media" => 1, _ => 2 })
            .ThenBy(v => v.Dates.Count > 0 ? v.Dates[0] : DateOnly.MinValue)
            .ThenBy(v => v.EmployeeName)
            .ToList();
        var hours = run.Hours.OrderBy(h => h.Semana).ThenBy(h => h.EmployeeName).ToList();
        return new ComplianceResult(Pct(total, violations), total, violations, porRegra, ordered, DateTime.UtcNow, hours,
            ctx.UsesRegimes ? ctx.WeekRegimes() : null);
    }

    /// <summary>Violações de regras rígidas (severidade alta ou média).</summary>
    public static int RigidViolations(ComplianceResult result) => result.Violacoes.Count(v => v.Severidade is "alta" or "media");

    /// <summary>
    /// Motivo para NÃO aprovar a escala (null = pode aprovar): nenhuma célula de trabalho no período (todos de folga), ou
    /// dia com a loja aberta e ninguém escalado no setor (<see cref="DiaSemNinguem"/>, rígida ou aviso). A aprovação de
    /// uma escala assim exige <paramref name="confirmed"/> (só os dias sem ninguém com a cobertura como meta; escala sem
    /// nenhum trabalho nunca é aprovada).
    /// </summary>
    public static string? ApprovalBlock(ComplianceResult result, int workCells, bool confirmed = false)
    {
        if (workCells == 0)
            return "A escala não tem nenhum turno de trabalho: todos estão de folga no período. Isso indica erro de configuração " +
                   "(horário da loja em Cadastro da loja, operação em Lojas → Operação ou turnos). Corrija e gere de novo; uma escala vazia não pode ser aprovada.";
        var empty = result.Violacoes.Where(v => v.Chave == DiaSemNinguem).ToList();
        if (empty.Count == 0) return null;
        var rigid = empty.Any(v => v.Severidade is "alta" or "media");
        if (confirmed && !rigid) return null;
        var dates = empty.SelectMany(v => v.Dates).Distinct().Order().ToList();
        return $"A escala tem {dates.Count} dia(s) com a loja aberta e ninguém escalado no setor " +
               $"({string.Join(", ", dates.Take(8).Select(d => d.ToString("dd/MM")))}{(dates.Count > 8 ? "..." : "")}). " +
               (rigid
                   ? "Gere de novo depois de conferir a configuração da loja/operação e o quadro; assim ela não pode ser aprovada."
                   : "O setor está com a cobertura como meta (quadro insuficiente): para aprovar assim mesmo, confirme a aprovação.");
    }
}

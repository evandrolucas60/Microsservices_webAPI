namespace Escala5x2.BuildingBlocks.Contracts.Scheduling;

/// <summary>
/// Componentes da função objetivo do motor (chaves do detalhamento de cada solução). O valor de cada componente é a
/// penalidade em pontos (peso × quantidade) na solução; <see cref="FolgasSeguidas"/> penaliza cada par de folgas em dias
/// seguidos (folgas intercaladas preferidas; com peso negativo no appsettings vira bônus, comportamento antigo).
/// <see cref="Peso"/> diz qual chave do appsettings do Worker (seção "Solver") mexe no componente.
/// </summary>
public static class EngineComponents
{
    public const string FaltaPico = "falta_pico";
    public const string ExcedenteBaixaDemanda = "excedente_baixa_demanda";
    public const string EquidadeDomingos = "equidade_domingos";
    public const string EquidadeAberturaFechamento = "equidade_abertura_fechamento";
    public const string FinsDeSemana = "fins_de_semana";
    public const string FolgasSeguidas = "folgas_seguidas";
    public const string MudancaHorario = "mudanca_horario";
    public const string Preferencias = "preferencias";
    public const string Custo = "custo";
    public const string HorasExtras = "horas_extras";
    public const string ViolacaoPreferencial = "violacao_preferencial";
    public const string FolgaExtraDomingo = "folga_extra_domingo";
    /// <summary>Rodízio de abertura/fechamento: turnos fora do perfil de jornada (peso por turno + reforço de pico neutralizado).</summary>
    public const string ForaDoPerfil = "fora_do_perfil";
    /// <summary>Rodízio de abertura/fechamento: (máximo − mínimo) dos turnos fora do perfil entre os elegíveis do setor.</summary>
    public const string EquidadeRodizio = "equidade_rodizio";
    /// <summary>
    /// Prioridade de folga dupla: peso × meses sem folga dupla de cada colaborador com prioridade que ficou SEM folga dupla
    /// no mês (custo de não atender; 0 = todos os priorizados atendidos).
    /// </summary>
    public const string FolgaDuplaPrioridade = "folga_dupla_prioridade";
    /// <summary>
    /// Setor de 2 pessoas: desvios das folgas fixas (trabalho em dia de folga fixa — inclusive a troca pelo domingo de
    /// folga —, folga fora do padrão) e diferença do padrão registrado na escala vizinha.
    /// </summary>
    public const string FolgasFixas = "folgas_fixas";
    /// <summary>
    /// Cobertura como meta: faltas no mínimo de cobertura do setor de 2 pessoas e do setor com quadro insuficiente (tempo
    /// descoberto, com as prioridades de pico/abertura/fechamento) e dos dias de borda sem escala vizinha.
    /// </summary>
    public const string FaltaCoberturaMeta = "falta_cobertura_meta";
    /// <summary>
    /// Intervalos escalonados: cada 15 min em que mais de uma pessoa do setor está em intervalo ao mesmo tempo (por pessoa
    /// a mais), para os intervalos se revezarem e o setor não esvaziar.
    /// </summary>
    public const string IntervalosSimultaneos = "intervalos_simultaneos";
    /// <summary>Só nas sugestões de correção.</summary>
    public const string ViolacaoRigida = "violacao_rigida";
    /// <summary>Só nas sugestões de correção.</summary>
    public const string FaltaCoberturaMinima = "falta_cobertura_minima";
    /// <summary>Só nas sugestões de correção.</summary>
    public const string CelulasAlteradas = "celulas_alteradas";

    /// <summary>Ordem de exibição.</summary>
    public static readonly string[] All =
    [
        FaltaPico, ExcedenteBaixaDemanda, EquidadeDomingos, EquidadeAberturaFechamento, FinsDeSemana, FolgasSeguidas, MudancaHorario,
        Preferencias, Custo, HorasExtras, ViolacaoPreferencial, FolgaExtraDomingo, ForaDoPerfil, EquidadeRodizio, FolgaDuplaPrioridade, FolgasFixas,
        FaltaCoberturaMeta, IntervalosSimultaneos, ViolacaoRigida, FaltaCoberturaMinima, CelulasAlteradas
    ];

    public static string Rotulo(string chave) => chave switch
    {
        FaltaPico => "Falta no pico / cobertura preferencial",
        ExcedenteBaixaDemanda => "Excedente em baixa demanda",
        EquidadeDomingos => "Equidade de domingos",
        EquidadeAberturaFechamento => "Equidade abertura/fechamento",
        FinsDeSemana => "Folgas no fim de semana (equidade)",
        FolgasSeguidas => "Folgas seguidas (pares; intercaladas preferidas)",
        MudancaHorario => "Mudança de horário",
        Preferencias => "Preferências",
        Custo => "Custo (minutos pagos + adicionais)",
        HorasExtras => "Horas extras",
        ViolacaoPreferencial => "Violação de regra preferencial",
        FolgaExtraDomingo => "Folga extra de domingo (6x1)",
        ForaDoPerfil => "Turnos fora do perfil (rodízio de abertura/fechamento)",
        EquidadeRodizio => "Equidade do rodízio de abertura/fechamento",
        FolgaDuplaPrioridade => "Folga dupla não atendida (prioridade por meses sem)",
        FolgasFixas => "Desvio das folgas fixas (setor de 2 pessoas)",
        FaltaCoberturaMeta => "Tempo descoberto (cobertura como meta: setor de 2 pessoas, quadro insuficiente e dias de borda)",
        IntervalosSimultaneos => "Intervalos ao mesmo tempo no setor (escalonamento)",
        ViolacaoRigida => "Violação de regra rígida",
        FaltaCoberturaMinima => "Falta no mínimo de cobertura",
        CelulasAlteradas => "Células alteradas",
        _ => chave
    };

    public static string Peso(string chave) => chave switch
    {
        FaltaPico => "WeightPeakDeficit (com operação) / WeightCoverageDeficitPreferential",
        ExcedenteBaixaDemanda => "WeightLowDemandExcess",
        EquidadeDomingos => "WeightSundayEquity",
        EquidadeAberturaFechamento => "WeightOpeningClosingEquity",
        FinsDeSemana => "WeightWeekendOffEquity",
        FolgasSeguidas => "WeightConsecutiveDaysOffPenalty",
        MudancaHorario => "WeightStartChangePerMinute",
        Preferencias => "WeightPreferenceDayOff, WeightPreferencePeriodPer15Min",
        Custo => "WeightPaidMinute, WeightCostPerCent, OvertimePremiumPercent, NightPremiumPercent",
        HorasExtras => "WeightOvertimeMinute",
        ViolacaoPreferencial => "WeightPreferentialViolation",
        FolgaExtraDomingo => "WeightExtraSundayDayOff",
        ForaDoPerfil => "WeightOutOfProfileShift, WeightOutOfProfileCrossShift (+ WeightPeakDeficit por faixa de pico coberta)",
        EquidadeRodizio => "WeightRotationEquity",
        FolgaDuplaPrioridade => "WeightDoubleDayOffPriority (× meses sem folga dupla, até DoubleDayOffPriorityMaxMonths)",
        FolgasFixas => "WeightFixedDayOffDeviation, WeightFixedDayOffContinuity",
        FaltaCoberturaMeta => "WeightCoverageTargetDeficit, WeightCoverageTargetOpeningClosing, WeightCoverageTargetTilt, WeightEdgeCoverageDeficit",
        IntervalosSimultaneos => "WeightBreakOverlapPer15Min",
        ViolacaoRigida => "WeightRigidViolation",
        FaltaCoberturaMinima => "WeightCoverageDeficitRigid",
        CelulasAlteradas => "WeightChangedCell",
        _ => ""
    };
}

/// <summary>Indicadores simples por pessoa numa solução: menor e maior valor entre os colaboradores.</summary>
public sealed record EngineRangeDto(int Min, int Max);

/// <summary>
/// Uma solução (melhorada) encontrada pelo CP-SAT durante a geração. <paramref name="Numero"/>: ordem em que foi
/// encontrada (1 = primeira); <paramref name="Segundos"/>: tempo decorrido; <paramref name="MelhorLimite"/>: melhor limite
/// inferior provado até ali (BestObjectiveBound); <paramref name="Gap"/>: (objetivo − limite) / |objetivo| (0 = ótimo provado).
/// <paramref name="Componentes"/>: penalidade por componente (<see cref="EngineComponents"/>);
/// <paramref name="Indicadores"/>: aberturas, fechamentos e domingos por pessoa (mín–máx). <paramref name="Escolhida"/>:
/// a solução entregue (a última/melhor).
/// </summary>
public sealed record EngineSolutionDto(
    int Numero,
    double Segundos,
    long Objetivo,
    double MelhorLimite,
    double Gap,
    IReadOnlyDictionary<string, long> Componentes,
    IReadOnlyDictionary<string, EngineRangeDto> Indicadores,
    bool Escolhida = false);

/// <summary>Componente presente no modelo: chave, rótulo e a chave de peso do appsettings que o controla.</summary>
public sealed record EngineComponentInfo(string Chave, string Rotulo, string Peso);

/// <summary>
/// Resumo das opções de escala avaliadas pelo motor numa geração. <paramref name="Total"/>: soluções encontradas (todas,
/// mesmo além do limite guardado); <paramref name="Guardadas"/>: quantas vêm em <paramref name="Solucoes"/> (as últimas,
/// até <paramref name="Limite"/> = Solver:MaxLoggedSolutions). <paramref name="Status"/>: Otima | Viavel.
/// <paramref name="Deslocamento"/>: constante dos componentes que não entra no objetivo do CP-SAT (regras preferenciais
/// contadas como peso × descumprimentos): objetivo = Σ componentes − deslocamento.
/// </summary>
public sealed record EngineSolutionsSummary(
    int Total,
    int Guardadas,
    int Limite,
    string Status,
    long? ObjetivoFinal,
    double? MelhorLimite,
    double? Gap,
    double Segundos,
    long Deslocamento,
    IReadOnlyList<EngineComponentInfo> Componentes,
    IReadOnlyList<EngineSolutionDto> Solucoes);

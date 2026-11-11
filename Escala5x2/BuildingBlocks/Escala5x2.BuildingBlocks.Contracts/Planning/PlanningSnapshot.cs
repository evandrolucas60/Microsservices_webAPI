namespace Escala5x2.BuildingBlocks.Contracts.Planning;

/// <summary>Regra de domingo aplicável à pessoa, definida pelo jurídico (nunca derivada do sexo no código).</summary>
public enum SundayRule
{
    /// <summary>Lei 10.101/2000, art. 6º, parágrafo único: 1 domingo livre a cada 3 semanas.</summary>
    OneInThreeWeeks = 0,
    /// <summary>CLT, art. 386: revezamento quinzenal, ativado por parecer jurídico.</summary>
    Biweekly = 1
}

public sealed record SectorSnapshot(Guid SectorId, string Name);

/// <summary>
/// Turno da fotografia. <paramref name="Tipo"/> (só com a operação da loja/setor configurada, ver
/// <see cref="OperationPlan"/>): Abertura | Fechamento | Flexivel (null = turno do catálogo fora da operação: só
/// manual). <paramref name="Gerado"/>: turno criado a partir da operação (não existe no catálogo do Store.Api; id
/// determinístico, ex.: "FX 09:15" para a entrada flexível das 09:15). Fotografias antigas não têm os dois campos.
/// </summary>
public sealed record ShiftSnapshot(Guid ShiftId, string Code, TimeOnly Start, TimeOnly End, int BreakMinutes,
    string? Tipo = null, bool Gerado = false);

/// <summary>
/// Demanda por faixa (StaffDemand) ou, com <paramref name="PicoManual"/>, horário de pico cadastrado à mão pelo usuário
/// (Store.Api, picos manuais; o pico da loja inteira vem repetido para cada setor). Pico manual:
/// <paramref name="Data"/> = só nessa data (prevalece sobre os picos do dia da semana; <paramref name="DayOfWeek"/> é o dia
/// da semana da data); <paramref name="MinStaff"/> &gt; 0 = pessoas desejadas na faixa (SUBSTITUI a demanda das linhas
/// comuns na faixa); 0 = mantém a demanda/mínimo; <paramref name="Adicional"/> = pessoas a mais sobre esse alvo.
/// <paramref name="Rotulo"/>: texto para avisos ("Pico manual 11:00–14:00 de sábado"). Fotografias antigas: só demanda.
/// </summary>
public sealed record DemandSnapshot(Guid SectorId, DayOfWeek DayOfWeek, TimeOnly Start, TimeOnly End, int MinStaff,
    bool PicoManual = false, DateOnly? Data = null, int? Adicional = null, string? Rotulo = null);

/// <summary>
/// Feriado aplicável à loja num dia do horizonte (recorrentes já expandidos para o ano do dia). Feriados de
/// abrangências diferentes na mesma data viram um só: nomes juntos ("Natal / ..."), <paramref name="Abrangencia"/>
/// com as abrangências separadas por vírgula ("Nacional, Municipal") e trabalho autorizado só se todos autorizam
/// (vale o mais restritivo). Fotografias antigas não têm abrangência (null).
/// <paramref name="Funcionamento"/> (nome de <see cref="FuncionamentoDia"/>; null = fotografia antiga): Fechada = loja
/// fechada, sem demanda e ninguém escalado; Especial = a janela de cobertura do dia fica restrita a
/// <paramref name="Abertura"/>–<paramref name="Fechamento"/>; Normal = horário da semana.
/// <paramref name="PontoFacultativo"/>: todos os feriados do dia são ponto facultativo.
/// </summary>
public sealed record HolidaySnapshot(DateOnly Date, string Name, bool WorkAuthorized, string? Abrangencia = null,
    string? Funcionamento = null, TimeOnly? Abertura = null, TimeOnly? Fechamento = null, bool PontoFacultativo = false);

/// <summary>Dia de ausência registrada no Employee.Api, com o tipo (<see cref="UnavailabilityKinds"/>). Sem observação (LGPD).</summary>
public sealed record UnavailabilitySnapshot(DateOnly Date, string Tipo);

/// <summary>
/// <paramref name="UnavailableDates"/>: todos os dias indisponíveis (férias, afastamentos, licenças...), como sempre.
/// <paramref name="Absences"/>: os mesmos dias com o tipo; fotografias antigas não têm (null) e continuam legíveis.
/// <paramref name="Jornada"/>: perfil de jornada, disponibilidade semanal, preferências e custo (null = fotografia
/// antiga: qualquer turno, sempre disponível, sem preferências).
/// <paramref name="JobTitle"/>: cargo do cadastro (página de consulta das escalas; null = não informado/fotografia antiga).
/// </summary>
public sealed record EmployeeSnapshot(
    Guid EmployeeId,
    string Registration,
    string Name,
    IReadOnlyList<Guid> SectorIds,
    Guid ContractVersionId,
    bool JourneyControlled,
    bool OvertimeAgreement,
    SundayRule SundayRule,
    IReadOnlyList<DateOnly> UnavailableDates,
    IReadOnlyList<UnavailabilitySnapshot>? Absences = null,
    EmployeeJourneySnapshot? Jornada = null,
    string? JobTitle = null);

/// <summary>Perfil de jornada do colaborador (cadastro do Employee.Api).</summary>
public static class JourneyProfiles
{
    /// <summary>Entra na abertura da loja/setor (turno de abertura da operação).</summary>
    public const string Abertura = "Abertura";
    /// <summary>Sai no fechamento (turno de fechamento da operação).</summary>
    public const string Fechamento = "Fechamento";
    /// <summary>Horários intermediários: entradas entre a mais cedo e a mais tarde, no incremento da operação.</summary>
    public const string Flexivel = "Flexivel";

    public static readonly string[] All = [Abertura, Fechamento, Flexivel];

    public static bool IsValid(string? perfil) => perfil is Abertura or Fechamento or Flexivel;

    public static string Rotulo(string? perfil) => perfil switch
    {
        Abertura => "Abertura",
        Fechamento => "Fechamento",
        Flexivel => "Flexível",
        _ => "Qualquer turno"
    };
}

/// <summary>
/// Disponibilidade num dia da semana: <paramref name="Disponivel"/> = pode trabalhar no dia; com
/// <paramref name="Inicio"/>/<paramref name="Fim"/> o turno inteiro precisa caber na janela (sem virar o dia).
/// Dia sem linha = disponível o dia inteiro.
/// </summary>
public sealed record AvailabilityWindow(DayOfWeek DiaSemana, bool Disponivel, TimeOnly? Inicio = null, TimeOnly? Fim = null);

/// <summary>Preferências opcionais (peso baixo na otimização): dias de folga preferidos e período (Cedo | Tarde).</summary>
public sealed record JourneyPreferences(IReadOnlyList<DayOfWeek>? FolgasPreferidas = null, string? PeriodoPreferido = null)
{
    public const string Cedo = "Cedo";
    public const string Tarde = "Tarde";
}

/// <summary>
/// Dados de jornada do colaborador na fotografia. <paramref name="CustoHora"/>: custo/hora (R$) para os custos
/// adicionais (hora extra, adicional noturno); null = não informado.
/// </summary>
public sealed record EmployeeJourneySnapshot(
    string PerfilJornada,
    IReadOnlyList<AvailabilityWindow>? Disponibilidade = null,
    JourneyPreferences? Preferencias = null,
    decimal? CustoHora = null);

/// <summary>Tipos de ausência (nomes do enum UnavailabilityType do Employee.Api) e rótulos exibidos.</summary>
public static class UnavailabilityKinds
{
    public const string Ferias = "Ferias";
    public const string Afastamento = "Afastamento";
    public const string Licenca = "Licenca";
    public const string Indisponivel = "Indisponivel";
    /// <summary>Afastamento por dispensa médica (atestado). Não se guarda CID nem diagnóstico (LGPD, dado sensível).</summary>
    public const string AtestadoMedico = "AtestadoMedico";

    /// <summary>Rótulo genérico mostrado a quem não pode ver o tipo (Colaborador).</summary>
    public const string RotuloGenerico = "Afastado";

    /// <summary>Quando dois registros cobrem o mesmo dia, vale o primeiro desta lista.</summary>
    public static readonly string[] Precedence = [AtestadoMedico, Afastamento, Licenca, Ferias, Indisponivel];

    public static string Rotulo(string? tipo) => tipo switch
    {
        Ferias => "Férias",
        Afastamento => "Afastamento",
        Licenca => "Licença",
        Indisponivel => "Indisponível",
        AtestadoMedico => "Afastamento médico",
        _ => "Ausência"
    };

    public static int Rank(string? tipo)
    {
        var i = Array.IndexOf(Precedence, tipo);
        return i < 0 ? Precedence.Length : i;
    }
}

/// <summary>
/// LEGADO: versão de regra legal (LegalRuleVersion) das fotografias gravadas antes do cadastro de regras de
/// conformidade. Só é lida para abrir/avaliar escalas antigas (convertida em ComplianceRuleSnapshot).
/// </summary>
public sealed record LegalRuleSnapshot(
    Guid RuleVersionId,
    string Code,
    DateOnly ValidFrom,
    DateOnly? ValidTo,
    int WeeklyLimitMinutes,
    int DailyLimitMinutes,
    int DailyLimitWithAgreementMinutes,
    int MinRestDaysPerWeek,
    int MaxConsecutiveWorkDays,
    int MinInterShiftRestMinutes,
    int SundayWindowWeeks,
    int MaxOvertimeMinutesPerDay);

/// <summary>
/// Dia trabalhado numa escala viva anterior (continuidade com a semana anterior) ou numa escala
/// publicada/aprovada POSTERIOR ao período (continuidade com a semana seguinte, <see cref="PlanningSnapshot.Following"/>).
/// </summary>
public sealed record HistoryEntry(Guid EmployeeId, DateOnly Date, TimeOnly Start, TimeOnly End);

/// <summary>
/// Folga de descanso registrada numa escala anterior/seguinte (continuidade da regra max_folgas_seguidas): dia em que a
/// escala que vale cobre a pessoa e ela não trabalha em nenhum setor, sem ausência nem feriado sem autorização nesse dia.
/// </summary>
public sealed record RestDayEntry(Guid EmployeeId, DateOnly Date);

/// <summary>
/// Pontuação de FOLGA DUPLA do colaborador (duas folgas de descanso em dias seguidos dentro do mesmo mês civil), calculada
/// pelo Scheduling.Api nos meses ANTERIORES ao da escala (escala principal do setor ou, sem ela, a Publicada/Aprovada):
/// <paramref name="MesesSemFolgaDupla"/> = meses seguidos, do mais recente para trás, sem nenhuma folga dupla (0 = teve no
/// mês anterior ou não há histórico). Mês com poucos dias de trabalho/folga registrados (férias, afastamento, sem escala)
/// é neutro: não conta nem interrompe a sequência. <paramref name="UltimaFolgaDupla"/>: primeiro dia da folga dupla mais
/// recente encontrada (null = nenhuma no histórico lido). <paramref name="MesesAvaliados"/>: meses com dados suficientes
/// lidos até parar (0 = sem histórico: pontuação neutra). O motor dá prioridade a quem tem mais meses sem folga dupla.
/// </summary>
public sealed record FolgaDuplaScore(Guid EmployeeId, int MesesSemFolgaDupla, DateOnly? UltimaFolgaDupla, int MesesAvaliados);

/// <summary>
/// Dia em que a pessoa já está alocada em outra escala viva (de outro setor) no mesmo período: não pode
/// trabalhar neste setor no mesmo dia e o turno conta para jornada e descanso.
/// </summary>
public sealed record OccupiedEntry(
    Guid EmployeeId, DateOnly Date, TimeOnly Start, TimeOnly End, int PaidMinutes, Guid SectorId, string SectorName, Guid ScheduleId);

/// <summary>
/// Fotografia imutável de tudo que o motor precisa. Fica gravada junto da escala.
/// Escala por setor: <see cref="Sectors"/> tem só o setor da escala, com a demanda dele e as pessoas habilitadas nele.
/// Regras: <see cref="ComplianceRules"/> (cadastro de regras de conformidade). Fotografias antigas têm só
/// <see cref="Rules"/> (LegalRuleSnapshot, legado) e continuam legíveis.
/// <see cref="Operacao"/>: operação do setor (horário, mínimos, entradas flexíveis); null = fotografia antiga ou setor
/// sem operação configurada (modelo anterior: só a demanda por faixa, qualquer pessoa em qualquer turno).
/// <see cref="Following"/>: dias trabalhados em escalas publicadas/aprovadas POSTERIORES ao período (até 3 semanas):
/// continuidade com a semana seguinte (dias seguidos, interjornada, folga isolada, domingos).
/// <see cref="History"/>: dias trabalhados nos 21 dias ANTERIORES, da melhor escala viva de cada setor e dia (Publicada &gt;
/// Aprovada &gt; Gerada/Rascunho mais recente). <see cref="HistoryNotices"/>: avisos sobre essa origem (semana anterior sem
/// escala cadastrada, histórico vindo de escala ainda não publicada); null = fotografia antiga.
/// <see cref="HistoryRest"/>/<see cref="FollowingRest"/>: folgas de descanso registradas na semana anterior/seguinte (só
/// dias que a escala que vale realmente cobre), para a regra max_folgas_seguidas na emenda; null = fotografia antiga (a
/// emenda não é verificada).
/// <see cref="FolgaDupla"/>: pontuação de folga dupla por colaborador (meses anteriores sem duas folgas seguidas), para o
/// motor priorizar quem está há mais tempo sem; null = fotografia antiga (sem prioridade).
/// <see cref="CoberturaMetaSetores"/>: setores em que a geração usou a cobertura mínima como META por quadro insuficiente
/// (<see cref="OperationSnapshot.CoberturaComoMetaQuadroInsuficiente"/>), gravados pelo Scheduling.Api com o resultado do
/// motor (<see cref="Scheduling.ScheduleGenerated.CoberturaMetaSetores"/>) para o avaliador tratar a faixa descoberta como
/// aviso, como o motor. O motor ignora o valor recebido (decide de novo a cada geração). null = nenhum/fotografia antiga.
/// <see cref="CoberturaMetaDias"/>: o mesmo só em alguns dias (dia com menos gente do que turnos — ausências, folgas
/// manuais —, num setor que o quadro cobre nos outros dias: a cobertura continua rígida neles).
/// </summary>
public sealed record PlanningSnapshot(
    Guid StoreId,
    string StoreName,
    bool StoreHasExtendedDayAgreement,
    IReadOnlyList<SectorSnapshot> Sectors,
    IReadOnlyList<ShiftSnapshot> Shifts,
    IReadOnlyList<DemandSnapshot> Demands,
    IReadOnlyList<HolidaySnapshot> Holidays,
    IReadOnlyList<EmployeeSnapshot> Employees,
    IReadOnlyList<LegalRuleSnapshot>? Rules,
    IReadOnlyList<HistoryEntry> History,
    IReadOnlyList<Compliance.ComplianceRuleSnapshot>? ComplianceRules = null,
    IReadOnlyList<OccupiedEntry>? Occupied = null,
    OperationSnapshot? Operacao = null,
    IReadOnlyList<HistoryEntry>? Following = null,
    IReadOnlyList<string>? HistoryNotices = null,
    IReadOnlyList<RestDayEntry>? HistoryRest = null,
    IReadOnlyList<RestDayEntry>? FollowingRest = null,
    IReadOnlyList<FolgaDuplaScore>? FolgaDupla = null,
    IReadOnlyList<Guid>? CoberturaMetaSetores = null,
    IReadOnlyList<CoberturaMetaDia>? CoberturaMetaDias = null);

/// <summary>Dia de um setor em que a geração usou a cobertura mínima como meta (menos gente do que turnos no dia).</summary>
public sealed record CoberturaMetaDia(Guid SectorId, DateOnly Date);

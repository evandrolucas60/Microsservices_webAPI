namespace Escala5x2.BuildingBlocks.Contracts.Compliance;

/// <summary>Ativo = aplicada na geração e na avaliação; Bloqueado = ignorada (aparece como "bloqueada" no avaliador).</summary>
public enum RuleStatus { Ativo, Bloqueado }

/// <summary>Rígida = restrição dura no motor; Preferencial = penalidade na otimização.</summary>
public enum RuleApplication { Rigida, Preferencial }

/// <summary>
/// Linha do cadastro de regras de conformidade levada na fotografia da escala. A mesma chave pode ter várias
/// linhas com vigências diferentes; sem vigência = vale sempre. Para cada dia vale a linha ATIVA que contém o dia.
/// <paramref name="BaseLegal"/>: citada nos avisos (fotografias antigas não têm; o avaliador usa um texto padrão).
/// </summary>
public sealed record ComplianceRuleSnapshot(
    int RuleId,
    string Categoria,
    string Chave,
    decimal Valor,
    string Unidade,
    string Descricao,
    DateOnly? VigenciaInicio,
    DateOnly? VigenciaFim,
    RuleStatus Status,
    RuleApplication Aplicacao,
    string? BaseLegal = null)
{
    public bool Contem(DateOnly dia) =>
        (VigenciaInicio is null || VigenciaInicio <= dia) && (VigenciaFim is null || VigenciaFim >= dia);
}

/// <summary>Chaves conhecidas pelo motor e pelo avaliador (snake_case, sem acento).</summary>
public static class RuleKeys
{
    /// <summary>Horas por semana civil (segunda a domingo) para quem tem controle de jornada.</summary>
    public const string CargaHorariaSemanal = "carga_horaria_semanal";
    /// <summary>Minutos pagos por dia sem hora extra.</summary>
    public const string LimiteDiario = "limite_diario_minutos";
    /// <summary>Vale no lugar do limite diário quando a loja tem norma coletiva (valor &gt; 0).</summary>
    public const string LimiteDiarioComAcordo = "limite_diario_com_acordo_minutos";
    /// <summary>Hora extra máxima por dia (só para quem tem acordo de prorrogação).</summary>
    public const string HoraExtraMaxDiaria = "hora_extra_max_diaria_minutos";
    /// <summary>Jornada líquida máxima num domingo.</summary>
    public const string LimiteDomingo = "limite_domingo_minutos";
    /// <summary>
    /// 1 = acordo de compensação semanal no 5x2 (CLT art. 59, §2º; Súmula 85 do TST): na semana civil 5x2 do modelo de regime o
    /// limite diário passa a ser o maior entre o limite diário (ou o com norma coletiva) e carga_horaria_semanal da semana ÷
    /// dias de trabalho do regime (7 − folgas_semana_5x2: 44h → 8h48, 42h → 8h24, 40h → 8h), no máximo limite diário +
    /// hora_extra_max_diaria_minutos (10h). A carga semanal continua valendo. Motor, verificação de configuração, turnos
    /// automáticos e avaliador usam o mesmo valor (PlanningContext.DailyLimit). 0/bloqueada/ausente: só o limite diário.
    /// </summary>
    public const string CompensacaoJornada5x2 = "compensacao_jornada_5x2";
    /// <summary>
    /// Piso legal do descanso semanal (DSR): folgas mínimas por semana civil. No modelo de regime prevalece sobre as
    /// folgas do regime (folgas da semana = máximo entre as duas).
    /// </summary>
    public const string FolgasMinimasSemana = "folgas_minimas_semana";

    // ---------- Regime da semana (6x1 / 5x2), escolhido pela carga semanal vigente ----------

    /// <summary>
    /// Limiar (horas): semana com carga_horaria_semanal vigente ≥ este valor é 6x1; abaixo dele, 5x2. Na semana de
    /// virada vale a carga mais restritiva (a menor). Bloqueada/ausente: toda semana é 5x2.
    /// </summary>
    public const string CargaMinimaRegime6x1 = "carga_minima_regime_6x1_horas";
    /// <summary>
    /// Número EXATO de folgas por colaborador e semana civil no regime 6x1 (dias trabalhados = dias normais − folgas).
    /// Folga = dia sem trabalho que NÃO é feriado sem autorização nem ausência (férias, afastamento...): esses dias
    /// reduzem os dias de trabalho em vez de virar folga.
    /// </summary>
    public const string FolgasSemana6x1 = "folgas_semana_6x1";
    /// <summary>Número EXATO de folgas por colaborador e semana civil no regime 5x2 (mesma conta do 6x1).</summary>
    public const string FolgasSemana5x2 = "folgas_semana_5x2";
    /// <summary>Máximo de dias de trabalho seguidos nos dias de semana 6x1 (OJ 410 da SDI-1 do TST: 6).</summary>
    public const string DiasConsecutivosMax6x1 = "dias_consecutivos_max_6x1";
    /// <summary>Máximo de dias de trabalho seguidos nos dias de semana 5x2.</summary>
    public const string DiasConsecutivosMax5x2 = "dias_consecutivos_max_5x2";

    /// <summary>
    /// 1 = na semana 6x1 em que a pessoa folga no DOMINGO pode haver 1 folga a mais que folgas_semana_6x1. Sem isso o
    /// 6x1 é inviável com o rodízio de domingos: com 1 folga por semana e no máximo 6 dias seguidos (OJ 410) o dia da
    /// folga só pode recuar na semana, e voltar ao domingo exige uma folga extra. O motor penaliza a folga extra (só usa
    /// quando necessário). 0/bloqueada = número exato sempre.
    /// </summary>
    public const string FolgaExtraDomingo6x1 = "folga_extra_domingo_6x1";

    /// <summary>
    /// 1 = na semana 5x2 as duas folgas são em dias SEGUIDOS da mesma semana civil (política interna; o 6x1 tem uma folga
    /// só e não se aplica). Semana com menos de 2 folgas (feriado sem autorização/ausência) não é verificada. Criada
    /// Bloqueada pela migration: o superAdmin ativa quando a política exigir.
    /// </summary>
    public const string FolgasConsecutivas5x2 = "folgas_consecutivas_5x2";

    /// <summary>
    /// Máximo de folgas SEGUIDAS (dias): em qualquer sequência de valor + 1 dias pelo menos um é trabalhado — folgas de
    /// preferência intercaladas, no máximo N juntas (política interna). Vale também na virada de semana e na emenda com a
    /// escala anterior/seguinte (folgas registradas nelas). Folga = dia sem trabalho que NÃO é feriado sem autorização nem
    /// ausência (férias, afastamento, atestado, indisponibilidade): esses dias interrompem a contagem (não são folga e o
    /// motor não tem como evitá-los). Dia sem escala anterior/seguinte cadastrada não é contado (não restringe).
    /// Compatível com folgas_consecutivas_5x2 (o par exigido tem 2 dias).
    /// </summary>
    public const string MaxFolgasSeguidas = "max_folgas_seguidas";

    /// <summary>Chaves do modelo de regime: a fotografia que tem alguma delas usa o regime por semana.</summary>
    public static readonly string[] Regime =
        [CargaMinimaRegime6x1, FolgasSemana6x1, FolgasSemana5x2, DiasConsecutivosMax6x1, DiasConsecutivosMax5x2, FolgaExtraDomingo6x1];

    // ---------- Operação da loja/setor (não são linhas do cadastro: vêm da operação configurada no Store.Api) ----------

    /// <summary>Cobertura mínima em cada faixa do expediente, contando só quem está FORA do intervalo (rígida).</summary>
    public const string CoberturaMinima = "cobertura_minima";
    /// <summary>Mínimo de pessoas na primeira faixa do expediente (abertura), rígida.</summary>
    public const string MinimoAbertura = "minimo_abertura";
    /// <summary>Mínimo de pessoas na última faixa do expediente (fechamento), rígida.</summary>
    public const string MinimoFechamento = "minimo_fechamento";
    /// <summary>Demanda por faixa acima do mínimo (reforço de pico): preferencial, entra na otimização como déficit.</summary>
    public const string ReforcoDemanda = "reforco_demanda";
    /// <summary>Perfil de jornada do colaborador (Abertura/Fechamento/Flexível): verificação informativa das células manuais.</summary>
    public const string PerfilJornada = "perfil_jornada";
    /// <summary>Disponibilidade semanal do colaborador (dias e horários em que pode trabalhar): rígida.</summary>
    public const string Disponibilidade = "disponibilidade";

    public static readonly string[] Operacao = [CoberturaMinima, MinimoAbertura, MinimoFechamento];

    // ---------- Aposentadas (só em fotografias antigas; o cadastro não aceita mais) ----------

    /// <summary>
    /// LEGADO (aposentada pela migration RegimePorCargaSemanal, convertida em folgas_semana_5x2): no máximo N folgas
    /// por semana; com folgas_minimas_semana dava exatamente N. Só é aplicada em fotografias sem o modelo de regime.
    /// </summary>
    public const string FolgasMaximasSemana = "folgas_maximas_semana";
    /// <summary>
    /// LEGADO (aposentada pela migration RegimePorCargaSemanal, convertida em dias_consecutivos_max_5x2 e _6x1): máximo
    /// de dias seguidos único. Só é aplicada em fotografias sem o modelo de regime.
    /// </summary>
    public const string DiasConsecutivosMax = "dias_consecutivos_max";

    /// <summary>Chaves aposentadas: não podem mais ser cadastradas (conflitariam com as do regime).</summary>
    public static readonly string[] Aposentadas = [FolgasMaximasSemana, DiasConsecutivosMax];
    public const string InterjornadaMinima = "interjornada_minima_minutos";
    /// <summary>Horas livres mínimas numa folga isolada (trabalho, folga, trabalho).</summary>
    public const string FolgaIsoladaHorasLivres = "folga_isolada_horas_livres";
    /// <summary>Folga em ao menos 1 domingo a cada N semanas (todos).</summary>
    public const string DomingoFolgaJanela = "domingo_folga_janela_semanas";
    /// <summary>Folga em ao menos 1 domingo a cada N semanas (quem tem regra quinzenal).</summary>
    public const string DomingoQuinzenalJanela = "domingo_quinzenal_janela_semanas";
    public const string IntervaloPadrao = "intervalo_padrao_minutos";
    public const string IntervaloMinimo = "intervalo_minimo_minutos";
    public const string IntervaloMaximo = "intervalo_maximo_minutos";
    /// <summary>Jornada (líquida) acima deste valor exige intervalo de refeição (CLT art. 71, caput).</summary>
    public const string IntervaloObrigatorioAcima = "intervalo_obrigatorio_acima_minutos";
    /// <summary>Intervalo mínimo para jornada acima de 4h e até o limite acima (CLT art. 71, §1º).</summary>
    public const string IntervaloJornada4a6h = "intervalo_jornada_4a6h_minutos";
    /// <summary>O intervalo começa pelo menos N minutos depois da entrada (política interna).</summary>
    public const string IntervaloInicioMinimoAposEntrada = "intervalo_inicio_minimo_apos_entrada_minutos";
    /// <summary>O intervalo termina pelo menos N minutos antes da saída (política interna).</summary>
    public const string IntervaloFimMinimoAntesSaida = "intervalo_fim_minimo_antes_saida_minutos";
    /// <summary>Nenhum trecho contínuo de trabalho, antes ou depois do intervalo, passa de N minutos.</summary>
    public const string JornadaContinuaMaxima = "jornada_continua_maxima_minutos";
    /// <summary>Tamanho da faixa de horário em que a cobertura da demanda é medida.</summary>
    public const string CoberturaFaixa = "cobertura_faixa_minutos";
    /// <summary>1 = trabalho em feriado só com autorização em convenção coletiva.</summary>
    public const string FeriadoExigeAutorizacao = "feriado_trabalho_exige_autorizacao";

    public static readonly string[] Unidades = ["minutos", "horas", "dias", "semanas", "quantidade", "booleano"];
}

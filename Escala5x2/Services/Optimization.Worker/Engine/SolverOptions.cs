namespace Optimization.Worker.Engine;

/// <summary>
/// Pesos da função objetivo e parâmetros do CP-SAT (appsettings, seção "Solver"). Não são regras de conformidade: os
/// parâmetros legais vêm do cadastro de regras na fotografia. Regras RÍGIDAS e o mínimo de cobertura nunca viram
/// penalidade na geração (exceções: setor de 2 pessoas, setor com quadro insuficiente e dias de borda, em que a cobertura
/// é meta — ver <see cref="CoverageTargetUnderstaffedSectors"/>,
/// <see cref="WeightCoverageTargetDeficit"/> e <see cref="WeightEdgeCoverageDeficit"/>); os pesos de violação de regra rígida
/// só valem nas sugestões de correção.
/// <para>Ordem de grandeza (por padrão): reforço de pico (20.000 por pessoa × faixa) ≫ equilíbrio (2.000 por unidade de
/// diferença entre o máximo e o mínimo) ≈ prioridade de folga dupla (1.000 por mês sem, até 3 meses) ≫ par de folgas seguidas (300, penalidade: folgas intercaladas) ≫ preferências (100 por folga fora do dia preferido,
/// 10 por 15 min fora do período preferido) ≫ excedente em baixa demanda (30 por pessoa × faixa) ≈ mudança de horário
/// (1 por minuto de diferença na entrada).</para>
/// </summary>
public sealed class SolverOptions
{
    public const string Section = "Solver";
    public int Workers { get; set; } = 8;
    /// <summary>Tempo máximo (s) do diagnóstico de inviabilidade (cobertura relaxada + núcleo de restrições), além do tempo da geração.</summary>
    public int DiagnosisSeconds { get; set; } = 30;
    /// <summary>Núcleo de restrições: tenta reduzir o conjunto de grupos em conflito quando ele tem até este tamanho.</summary>
    public int DiagnosisMaxCoreToMinimize { get; set; } = 12;
    /// <summary>
    /// Registro das soluções da geração: quantas das últimas soluções encontradas pelo CP-SAT são guardadas (com o
    /// detalhamento do objetivo) no log e na escala. O total encontrado é sempre contado. 0 = só conta, sem detalhar.
    /// </summary>
    public int MaxLoggedSolutions { get; set; } = 50;

    public int WeightPaidMinute { get; set; } = 1;
    public int WeightOvertimeMinute { get; set; } = 2;
    /// <summary>Equilíbrio de domingos trabalhados (máximo − mínimo entre colaboradores).</summary>
    public int WeightSundayEquity { get; set; } = 2000;
    /// <summary>Equilíbrio de aberturas e de fechamentos (máximo − mínimo entre quem pode abrir/fechar).</summary>
    public int WeightOpeningClosingEquity { get; set; } = 2000;
    /// <summary>Equilíbrio de folgas em sábado/domingo (máximo − mínimo).</summary>
    public int WeightWeekendOffEquity { get; set; } = 1000;
    /// <summary>
    /// Folgas intercaladas: cada par de folgas de descanso em dias seguidos (inclusive na virada de semana e na emenda
    /// com a escala anterior/seguinte). Positivo (padrão) = penalidade leve, prefere folgas intercaladas; 0 = indiferente;
    /// negativo = bônus ao par (comportamento antigo, que favorecia folgas juntas). O limite rígido de folgas seguidas é a
    /// regra max_folgas_seguidas do cadastro.
    /// </summary>
    public int WeightConsecutiveDaysOffPenalty { get; set; } = 300;
    /// <summary>
    /// LEGADO (substituído por <see cref="WeightConsecutiveDaysOffPenalty"/>; era o bônus a pares de folgas seguidas):
    /// ignorado, mantido para não quebrar configurações antigas.
    /// </summary>
    public int WeightConsecutiveDaysOff { get; set; } = 300;
    /// <summary>Mudança de horário: cada minuto de diferença na entrada entre dias trabalhados seguidos.</summary>
    public int WeightStartChangePerMinute { get; set; } = 1;
    /// <summary>LEGADO (substituído por <see cref="WeightStartChangePerMinute"/>): mantido para não quebrar configurações antigas.</summary>
    public int WeightShiftChange { get; set; } = 50;
    /// <summary>Cada pessoa faltando numa faixa com demanda acima do mínimo (reforço de pico, com operação).</summary>
    public int WeightPeakDeficit { get; set; } = 20_000;
    /// <summary>
    /// Cada pessoa faltando numa faixa de PICO MANUAL (horário de pico cadastrado pelo usuário). Um pouco acima do reforço
    /// de pico calculado: entre os dois, o motor atende primeiro o que o usuário cadastrou. Também entra no custo do turno
    /// fora do perfil (o pico manual, como o reforço, nunca justifica sozinho sair do perfil).
    /// </summary>
    public int WeightManualPeakDeficit { get; set; } = 25_000;
    /// <summary>Cada pessoa acima da demanda numa faixa (excedente em baixa demanda, com operação).</summary>
    public int WeightLowDemandExcess { get; set; } = 30;
    /// <summary>Preferência: cada dia trabalhado num dia de folga preferido.</summary>
    public int WeightPreferenceDayOff { get; set; } = 100;
    /// <summary>Preferência de período (Cedo/Tarde): cada 15 min de distância da entrada preferida.</summary>
    public int WeightPreferencePeriodPer15Min { get; set; } = 10;
    /// <summary>Custos adicionais (de quem tem custo/hora): peso por centavo de real.</summary>
    public int WeightCostPerCent { get; set; } = 1;
    /// <summary>Adicional de hora extra sobre o custo/hora (CLT art. 59, §1º: no mínimo 50%).</summary>
    public int OvertimePremiumPercent { get; set; } = 50;
    /// <summary>Adicional noturno sobre o custo/hora (CLT art. 73: 20%, das 22h às 5h).</summary>
    public int NightPremiumPercent { get; set; } = 20;
    /// <summary>Cada verificação de regra preferencial descumprida (inclusive posição do intervalo fora da recomendada).</summary>
    public int WeightPreferentialViolation { get; set; } = 20_000;
    /// <summary>Sugestões de correção: cada verificação de regra rígida descumprida.</summary>
    public int WeightRigidViolation { get; set; } = 1_000_000;
    /// <summary>Sugestões de correção: cada pessoa faltando no mínimo de cobertura.</summary>
    public int WeightCoverageDeficitRigid { get; set; } = 100_000;
    /// <summary>Cada pessoa faltando numa faixa de cobertura preferencial (sem operação).</summary>
    public int WeightCoverageDeficitPreferential { get; set; } = 20_000;
    /// <summary>
    /// Regime 6x1: cada folga extra de domingo (folga_extra_domingo_6x1) usada. Bem acima da economia de um turno, para
    /// a folga extra só aparecer quando o rodízio de domingos/dias seguidos exigir.
    /// </summary>
    public int WeightExtraSundayDayOff { get; set; } = 50_000;
    /// <summary>Sugestões: cada célula alterada (mudança mínima).</summary>
    public int WeightChangedCell { get; set; } = 5_000;

    /// <summary>
    /// Rodízio de abertura/fechamento (operação com RodizioAberturaFechamento ligado): cada turno fora do perfil de quem é
    /// Flexível pegando abertura/fechamento, ou de quem é Abertura/Fechamento pegando um intermediário (só em setor sem
    /// nenhum Flexível, para cobrir almoços). Alto, para o motor só sair do perfil quando o perfil não basta. Além do peso, o turno fora do perfil paga
    /// <see cref="WeightPeakDeficit"/> por faixa de reforço de pico que cobre: o reforço de pico (preferencial) nunca
    /// justifica sair do perfil — só o mínimo rígido de cobertura.
    /// </summary>
    public int WeightOutOfProfileShift { get; set; } = 5_000;
    /// <summary>Rodízio: cada turno de quem é Abertura pegando fechamento (ou Fechamento pegando abertura).</summary>
    public int WeightOutOfProfileCrossShift { get; set; } = 8_000;
    /// <summary>
    /// Rodízio justo: (máximo − mínimo) das aberturas e dos fechamentos fora do perfil entre os colaboradores elegíveis do
    /// setor (os Flexível, quando há; senão todos os de outro perfil), para distribuir em vez de sobrecarregar um.
    /// </summary>
    public int WeightRotationEquity { get; set; } = 3_000;

    /// <summary>
    /// Prioridade de folga dupla (duas folgas seguidas dentro do mês): cada colaborador com N meses seguidos sem folga dupla
    /// (pontuação da fotografia, calculada pelo Scheduling.Api) que fica SEM folga dupla no mês custa
    /// peso × min(N, <see cref="DoubleDayOffPriorityMaxMonths"/>). Quem está há mais tempo sem tem mais prioridade, e o
    /// rodízio passa por todos do setor mês a mês. Acima de <see cref="WeightConsecutiveDaysOffPenalty"/> (a folga dupla do
    /// priorizado vence a preferência por folgas intercaladas) e, no teto, abaixo do turno fora do perfil e do reforço de
    /// pico — o mínimo de cobertura e as regras rígidas nunca cedem (são restrições duras). 0 = desliga a prioridade.
    /// </summary>
    public int WeightDoubleDayOffPriority { get; set; } = 1_000;
    /// <summary>Teto de meses sem folga dupla considerados no peso da prioridade (padrão 3: até 3.000 por pessoa).</summary>
    public int DoubleDayOffPriorityMaxMonths { get; set; } = 3;

    /// <summary>
    /// Folgas fixas e intercaladas em setor com exatamente 2 colaboradores disponíveis (quando a operação liga
    /// FolgasFixasSetorDuasPessoas, o padrão). false = desliga em todas as lojas (folgas variáveis, comportamento anterior).
    /// </summary>
    public bool FixedDaysOffTwoPersonSectors { get; set; } = true;
    /// <summary>
    /// Folgas fixas: cada dia de folga fixa trabalhado e cada folga fora do padrão (semana com ausência/feriado/célula
    /// manual). O dia de troca pelo domingo de folga custa a metade (o rodízio de domingos da regra é obrigatório; a
    /// metade faz o domingo de folga trocar sempre o mesmo dia). Acima do equilíbrio de domingos/fins de semana e das
    /// preferências: o padrão só cede às regras rígidas e ao mínimo de cobertura (restrições duras).
    /// </summary>
    public int WeightFixedDayOffDeviation { get; set; } = 8_000;
    /// <summary>
    /// Folgas fixas: cada dia da escala vizinha (dias de borda e a semana antes/depois do mês) com folga fora do padrão ou
    /// trabalho num dia do padrão. Mantém o mesmo padrão de um mês para o outro (0 = escolha livre a cada mês).
    /// </summary>
    public int WeightFixedDayOffContinuity { get; set; } = 1_500;

    /// <summary>
    /// COBERTURA COMO META no setor de 2 pessoas (PlanningContext.SoftCoverage): cada pessoa faltando no mínimo de uma faixa
    /// de 30 min (proporcional ao tamanho da faixa). Prioridades do tempo coberto, da maior para a menor: (1) pico manual
    /// (este peso + <see cref="WeightManualPeakDeficit"/> por pessoa do pico), (2) horários de maior demanda (+
    /// <see cref="WeightPeakDeficit"/>), (3) abertura e fechamento (+ <see cref="WeightCoverageTargetOpeningClosing"/>),
    /// (4) as demais faixas, (5) desempate pela ponta do dia de <see cref="CoverageTargetSinglePreference"/>. Assim, nos dias
    /// em que os dois trabalham saem turnos de abertura + fechamento cobrindo o dia, e no dia em que só um trabalha ele
    /// pega o turno da janela mais importante. Acima das folgas fixas e do equilíbrio: só as regras rígidas limitam.
    /// </summary>
    public int WeightCoverageTargetDeficit { get; set; } = 10_000;
    /// <summary>Meta de cobertura (setor de 2 pessoas): adicional por pessoa faltando na primeira/última faixa do expediente.</summary>
    public int WeightCoverageTargetOpeningClosing { get; set; } = 5_000;
    /// <summary>
    /// Meta de cobertura (setor de 2 pessoas): desempate entre janelas de mesmo valor — até este peso por faixa, maior na
    /// ponta do dia preferida (<see cref="CoverageTargetSinglePreference"/>) e zero na outra.
    /// </summary>
    public int WeightCoverageTargetTilt { get; set; } = 300;
    /// <summary>
    /// Meta de cobertura (setor de 2 pessoas), dia em que só um trabalha e as janelas empatam (sem pico nem demanda que
    /// decida): "abertura" (padrão: a pessoa abre e a tarde/noite fica descoberta) ou "fechamento" (a pessoa fecha).
    /// </summary>
    public string CoverageTargetSinglePreference { get; set; } = "abertura";
    /// <summary>
    /// Dia de borda (semana parcial do mês vizinho sem escala vizinha): cada pessoa faltando no mínimo da faixa de 30 min. A
    /// cobertura desses dias não é desta escala (não é gravada): é só orientação para a semana parcial, nunca restrição dura.
    /// </summary>
    public int WeightEdgeCoverageDeficit { get; set; } = 2_000;

    /// <summary>
    /// Cobertura como meta no setor com QUADRO INSUFICIENTE (PlanningContext.CoverageTarget), quando a operação liga
    /// CoberturaComoMetaQuadroInsuficiente (o padrão): a contagem ou o CP-SAT provam que o mínimo não fecha e a escala sai
    /// cobrindo o máximo possível, com os mesmos pesos da meta do setor de 2 pessoas. false = desliga em todas as lojas
    /// (inviável com diagnóstico, comportamento anterior).
    /// </summary>
    public bool CoverageTargetUnderstaffedSectors { get; set; } = true;
    /// <summary>
    /// Cobertura como meta por quadro insuficiente: dá ao CP-SAT uma escala de partida (folgas espalhadas, um abre, outro
    /// fecha, os demais no intermediário). false = sem dica (diagnóstico de desempenho).
    /// </summary>
    public bool CoverageTargetHints { get; set; } = true;
    /// <summary>
    /// Intervalos escalonados no setor com cobertura como meta por quadro insuficiente: cada 15 min em que mais de uma pessoa
    /// do setor está em intervalo ao mesmo tempo (por pessoa a mais). Leve: a meta de cobertura já põe o intervalo onde há
    /// mais alguém no setor; este peso só faz os intervalos se revezarem quando sobra gente. Com a cobertura rígida não entra
    /// (o mínimo e o reforço de pico decidem; o termo deixava a busca de meses com muita gente lenta). 0 = desliga.
    /// </summary>
    public int WeightBreakOverlapPer15Min { get; set; } = 200;
}

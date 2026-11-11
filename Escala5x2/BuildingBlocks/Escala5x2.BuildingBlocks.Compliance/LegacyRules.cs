using Escala5x2.BuildingBlocks.Contracts.Compliance;
using Escala5x2.BuildingBlocks.Contracts.Planning;

namespace Escala5x2.BuildingBlocks.Compliance;

/// <summary>
/// Converte as versões de regra legal das fotografias antigas (LegalRuleSnapshot) nas linhas do cadastro de
/// regras de conformidade, com as mesmas vigências e os parâmetros que antes eram fixos no motor
/// (faixa de 30 min, folga isolada = 24h + interjornada, quinzenal = 2 semanas, feriado exige autorização).
/// Regras novas (limite de domingo, intervalo e posição do almoço) não existiam: escalas antigas não são avaliadas por
/// elas. O mesmo vale para fotografias do cadastro gravadas antes das regras de posição do almoço: só as chaves que
/// estão na fotografia são verificadas, e células sem início de intervalo contam presentes o turno inteiro.
/// </summary>
public static class LegacyRules
{
    public static IReadOnlyList<ComplianceRuleSnapshot> Convert(IEnumerable<LegalRuleSnapshot>? legacy)
    {
        var list = new List<ComplianceRuleSnapshot>();
        var id = -1; // ids negativos: não existem no cadastro
        foreach (var v in legacy ?? [])
        {
            DateOnly? from = v.ValidFrom;
            var to = v.ValidTo;
            void Add(string categoria, string chave, decimal valor, string unidade, string descricao) =>
                list.Add(new ComplianceRuleSnapshot(id--, categoria, chave, valor, unidade, $"{descricao} ({v.Code})",
                    from, to, RuleStatus.Ativo, RuleApplication.Rigida));

            Add("jornada", RuleKeys.CargaHorariaSemanal, v.WeeklyLimitMinutes / 60m, "horas", "Máximo de horas por semana");
            Add("jornada", RuleKeys.LimiteDiario, v.DailyLimitMinutes, "minutos", "Jornada diária máxima");
            if (v.DailyLimitWithAgreementMinutes > 0)
                Add("jornada", RuleKeys.LimiteDiarioComAcordo, v.DailyLimitWithAgreementMinutes, "minutos", "Jornada diária máxima com norma coletiva");
            Add("jornada", RuleKeys.HoraExtraMaxDiaria, v.MaxOvertimeMinutesPerDay, "minutos", "Hora extra máxima por dia");
            Add("descanso", RuleKeys.FolgasMinimasSemana, v.MinRestDaysPerWeek, "dias", "Folgas mínimas na semana");
            Add("descanso", RuleKeys.DiasConsecutivosMax, v.MaxConsecutiveWorkDays, "dias", "Máximo de dias seguidos");
            Add("descanso", RuleKeys.InterjornadaMinima, v.MinInterShiftRestMinutes, "minutos", "Descanso mínimo entre jornadas");
            Add("descanso", RuleKeys.FolgaIsoladaHorasLivres, (24 * 60 + v.MinInterShiftRestMinutes) / 60m, "horas", "Horas livres numa folga isolada");
            Add("domingo", RuleKeys.DomingoFolgaJanela, v.SundayWindowWeeks, "semanas", "Folga em 1 domingo a cada N semanas");
            Add("domingo", RuleKeys.DomingoQuinzenalJanela, 2, "semanas", "Revezamento quinzenal de domingos");
            Add("cobertura", RuleKeys.CoberturaFaixa, 30, "minutos", "Faixa de medição da cobertura");
            Add("feriado", RuleKeys.FeriadoExigeAutorizacao, 1, "booleano", "Feriado só com autorização");
        }
        return list;
    }

    /// <summary>Regras da fotografia: o cadastro novo, ou as versões legais convertidas (fotografias antigas).</summary>
    public static IReadOnlyList<ComplianceRuleSnapshot> Of(PlanningSnapshot snapshot) =>
        snapshot.ComplianceRules is { Count: > 0 } rules ? rules : Convert(snapshot.Rules);
}

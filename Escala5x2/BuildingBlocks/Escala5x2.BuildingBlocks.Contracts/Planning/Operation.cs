using System.Security.Cryptography;
using System.Text;

namespace Escala5x2.BuildingBlocks.Contracts.Planning;

/// <summary>
/// Operação de um setor (configurada no Store.Api por loja, com exceção opcional por setor), levada na fotografia.
/// <list type="bullet">
/// <item><paramref name="Abertura"/>/<paramref name="Fechamento"/>: expediente (sem virar o dia). A cobertura mínima vale
/// em todas as faixas de [abertura, fechamento) de todos os dias que a loja abre (feriado sem autorização: fechada).</item>
/// <item><paramref name="CoberturaMinima"/>: pessoas presentes FORA do intervalo em cada faixa (RÍGIDA).</item>
/// <item><paramref name="MinimoAbertura"/>/<paramref name="MinimoFechamento"/>: presentes na primeira/última faixa do
/// expediente (RÍGIDAS).</item>
/// <item><paramref name="IncrementoFlexivelMinutos"/> (15, 20 ou 30) e a janela de entrada flexível
/// (<paramref name="EntradaFlexivelMaisCedo"/>..<paramref name="EntradaFlexivelMaisTarde"/>; null = abertura + incremento /
/// fechamento − duração − incremento): entradas dos turnos do perfil Flexível.</item>
/// <item><paramref name="DuracaoJornadaMinutos"/>: duração dos turnos da operação COM o intervalo (500 = 8h20).</item>
/// <item><paramref name="IntervaloMinutos"/>: intervalo dos turnos gerados; null = intervalo_padrao_minutos do cadastro de regras.</item>
/// <item><paramref name="RodizioAberturaFechamento"/> (padrão LIGADO; fotografias antigas, sem o campo, também): quando
/// faltar gente com o perfil, qualquer colaborador do setor pode receber turno de outro tipo que o do perfil (abertura,
/// fechamento ou intermediário), com penalidade alta por turno e equidade do rodízio entre o setor. Desligado: o perfil
/// é restrição dura (Abertura só abre, Fechamento só fecha, Flexível só intermediários).</item>
/// <item><paramref name="Horarios"/>: horário de funcionamento da loja por dia da semana (cadastro da loja). Na operação
/// da loja, o expediente de cada dia é o do dia (<see cref="OperationPlan.DayWindow"/>; dia sem linha = abertura/fechamento
/// acima); dia Fechada = sem cobertura e ninguém escalado. Na exceção de um setor, só os dias em que a loja fecha
/// (o setor mantém o próprio expediente). null = fotografia antiga ou loja sem horário cadastrado (todos os dias iguais).</item>
/// <item><paramref name="FolgasFixasSetorDuasPessoas"/> (padrão LIGADO; fotografias antigas, sem o campo, também): setor
/// com exatamente 2 colaboradores disponíveis no mês recebe folgas FIXAS (os mesmos dias da semana toda semana) e
/// INTERCALADAS (as folgas dos dois nunca coincidem e, sem folgas_consecutivas_5x2 ativa, as de cada um não ficam em dias
/// seguidos); o domingo de folga exigido pela regra de domingos entra em rodízio no lugar de uma folga fixa. Ver
/// PlanningContext.FixedDaysOff. Desligado: folgas variáveis, como nos demais setores.</item>
/// <item><paramref name="CoberturaComoMetaQuadroInsuficiente"/> (padrão LIGADO; fotografias antigas, sem o campo, também):
/// "Gerar mesmo quando o quadro não cobre o mínimo". Setor em que a cobertura mínima (inclusive a dos intervalos,
/// abertura/fechamento) é impossível com o quadro — provado por contagem ou pelo motor — tem a cobertura como META: a
/// escala sai cobrindo o máximo possível e avisa as faixas descobertas; as regras legais continuam rígidas. Ver
/// PlanningContext.CoverageTarget. Desligado: a escala sai inviável com o diagnóstico.</item>
/// <item><paramref name="TurnosAutomaticos"/> (Store.Api: padrão LIGADO; fotografias antigas, sem o campo: DESLIGADO, para
/// continuarem avaliadas com os turnos que tinham): os turnos são DERIVADOS do expediente de cada dia (horário da loja,
/// feriado com horário especial) e das regras vigentes em cada dia — jornada = carga semanal ÷ dias de trabalho do regime,
/// limitada pelo limite diário (com a compensação do 5x2) e pelo de domingo, e encurtada quando o expediente do dia é menor;
/// intervalo pelas regras de intervalo. Abertura começa na abertura do dia, fechamento termina no fechamento e os
/// intermediários entram de <paramref name="IncrementoFlexivelMinutos"/> em <paramref name="IncrementoFlexivelMinutos"/>
/// minutos (ver PlanningContext.AutoShifts). <paramref name="DuracaoJornadaMinutos"/>, <paramref name="IntervaloMinutos"/>
/// e a janela flexível ficam guardados, mas só valem com o campo desligado ("Personalizar turnos"); os turnos do catálogo
/// ficam só para edição manual.</item>
/// </list>
/// </summary>
public sealed record OperationSnapshot(
    TimeOnly Abertura,
    TimeOnly Fechamento,
    int CoberturaMinima,
    int MinimoAbertura,
    int MinimoFechamento,
    int IncrementoFlexivelMinutos,
    TimeOnly? EntradaFlexivelMaisCedo,
    TimeOnly? EntradaFlexivelMaisTarde,
    int DuracaoJornadaMinutos,
    int? IntervaloMinutos,
    bool RodizioAberturaFechamento = true,
    IReadOnlyList<DayHours>? Horarios = null,
    bool FolgasFixasSetorDuasPessoas = true,
    bool CoberturaComoMetaQuadroInsuficiente = true,
    bool TurnosAutomaticos = false);

/// <summary>
/// Horário de funcionamento num dia da semana. <paramref name="Fechada"/>: a loja não abre no dia. Aberta sem
/// <paramref name="Abertura"/>/<paramref name="Fechamento"/>: usa o expediente da operação.
/// </summary>
public sealed record DayHours(DayOfWeek DiaSemana, bool Fechada, TimeOnly? Abertura = null, TimeOnly? Fechamento = null);

/// <summary>Operação efetiva de um setor no endpoint interno de planejamento (a do setor, ou a da loja).</summary>
public sealed record SectorOperation(Guid SectorId, OperationSnapshot Operacao, bool PorSetor);

/// <summary>
/// Regras da operação: validação, janela flexível efetiva e turnos candidatos por perfil.
/// <para><b>Turnos da operação</b> (decisão): o catálogo de turnos (Shift) continua valendo. Cada turno é classificado:
/// começa na abertura → Abertura; termina no fechamento → Fechamento; começa dentro da janela flexível, numa entrada do
/// incremento (contado a partir da abertura) e tem a duração da operação → Flexível; os demais ficam sem tipo (só
/// manual). Faltando turno de abertura/fechamento com a duração da operação, ou alguma entrada flexível, o turno é GERADO
/// na fotografia ("AB 07:00", "FE 13:40", "FX 09:15"), com id determinístico (mesma operação → mesmo id) e o intervalo
/// da operação. Turnos gerados não vão para o catálogo do Store.Api; aparecem na escala com o tipo e o horário.</para>
/// </summary>
public static class OperationPlan
{
    public const int DuracaoPadraoMinutos = 500;
    public static readonly int[] Incrementos = [15, 20, 30];

    public static int Min(TimeOnly t) => t.Hour * 60 + t.Minute;
    public static TimeOnly Time(int minutes) => new(minutes / 60 % 24, minutes % 60);
    public static string Hm(int minutes) => $"{minutes / 60 % 24:00}:{minutes % 60:00}";

    private static int Duration(TimeOnly start, TimeOnly end)
    {
        var s = Min(start);
        var e = Min(end);
        if (e <= s) e += 24 * 60;
        return e - s;
    }

    /// <summary>Entrada flexível mais cedo (padrão: abertura + incremento), em minutos do dia.</summary>
    public static int EarliestFlex(OperationSnapshot op) =>
        op.EntradaFlexivelMaisCedo is { } t ? Min(t) : Min(op.Abertura) + op.IncrementoFlexivelMinutos;

    /// <summary>Entrada flexível mais tarde (padrão: fechamento − duração − incremento), em minutos do dia.</summary>
    public static int LatestFlex(OperationSnapshot op) =>
        op.EntradaFlexivelMaisTarde is { } t ? Min(t) : Min(op.Fechamento) - op.DuracaoJornadaMinutos - op.IncrementoFlexivelMinutos;

    /// <summary>Entradas flexíveis (minutos do dia): da mais cedo à mais tarde, no incremento.</summary>
    public static List<int> FlexEntries(OperationSnapshot op)
    {
        var list = new List<int>();
        if (op.IncrementoFlexivelMinutos <= 0) return list;
        for (var e = EarliestFlex(op); e <= LatestFlex(op); e += op.IncrementoFlexivelMinutos) list.Add(e);
        return list;
    }

    /// <summary>Erros de validação (campo, mensagem em pt-BR). Vazio = operação válida.</summary>
    public static List<(string Campo, string Mensagem)> Validate(OperationSnapshot op)
    {
        var errors = new List<(string, string)>();
        var open = Min(op.Abertura);
        var close = Min(op.Fechamento);
        if (open >= close) errors.Add(("fechamento", "A abertura precisa ser antes do fechamento (o expediente não pode virar o dia)."));
        if (!Incrementos.Contains(op.IncrementoFlexivelMinutos))
            errors.Add(("incrementoFlexivelMinutos", "O incremento das entradas flexíveis é de 15, 20 ou 30 minutos."));
        if (op.CoberturaMinima < 1 || op.CoberturaMinima > 100) errors.Add(("coberturaMinima", "A cobertura mínima é de pelo menos 1 pessoa."));
        if (op.MinimoAbertura < 1 || op.MinimoAbertura > 100) errors.Add(("minimoAbertura", "O mínimo na abertura é de pelo menos 1 pessoa."));
        if (op.MinimoFechamento < 1 || op.MinimoFechamento > 100) errors.Add(("minimoFechamento", "O mínimo no fechamento é de pelo menos 1 pessoa."));
        // Turnos automáticos: jornada, intervalo e entradas vêm do horário de cada dia e das regras (nada a validar aqui;
        // a duração/intervalo personalizados guardados só valem com "Personalizar turnos")
        if (op.TurnosAutomaticos) return errors;
        if (op.DuracaoJornadaMinutos < 60 || op.DuracaoJornadaMinutos > 720)
            errors.Add(("duracaoJornadaMinutos", "A duração da jornada (com o intervalo) fica entre 60 e 720 minutos."));
        else if (open < close && op.DuracaoJornadaMinutos > close - open)
            errors.Add(("duracaoJornadaMinutos", $"A jornada de {Hm(op.DuracaoJornadaMinutos)} não cabe no expediente ({Hm(open)}–{Hm(close)})."));
        if (op.IntervaloMinutos is { } brk && (brk < 0 || brk > 240 || brk >= op.DuracaoJornadaMinutos))
            errors.Add(("intervaloMinutos", "O intervalo fica entre 0 e 240 minutos e menor que a jornada."));
        if (errors.Count > 0) return errors;

        var earliest = EarliestFlex(op);
        var latest = LatestFlex(op);
        if (earliest < open)
            errors.Add(("entradaFlexivelMaisCedo", $"A entrada flexível mais cedo ({Hm(earliest)}) é antes da abertura ({Hm(open)})."));
        if (latest + op.DuracaoJornadaMinutos > close)
            errors.Add(("entradaFlexivelMaisTarde",
                $"Quem entra às {Hm(latest)} sai às {Hm(latest + op.DuracaoJornadaMinutos)}, depois do fechamento ({Hm(close)})."));
        if (earliest > latest)
            errors.Add(("entradaFlexivelMaisTarde",
                $"A janela de entrada flexível ficou vazia ({Hm(earliest)} a {Hm(latest)}): ajuste as entradas, a duração ou o expediente."));
        return errors;
    }

    /// <summary>
    /// Expediente do dia da semana (minutos do dia) pelos <see cref="OperationSnapshot.Horarios"/>: o do dia, ou o da
    /// operação quando o dia não tem linha/horário. null = a loja não abre no dia.
    /// </summary>
    public static (int Open, int Close)? DayWindow(OperationSnapshot op, DayOfWeek day)
    {
        var h = op.Horarios?.FirstOrDefault(x => x.DiaSemana == day);
        if (h is { Fechada: true }) return null;
        return h is { Abertura: { } a, Fechamento: { } c } && Min(a) < Min(c) ? (Min(a), Min(c)) : (Min(op.Abertura), Min(op.Fechamento));
    }

    /// <summary>
    /// Expedientes distintos além do padrão da operação: os dos <see cref="OperationSnapshot.Horarios"/> e os
    /// <paramref name="extra"/> (horário especial de feriado), para gerar turnos de abertura/fechamento de cada um.
    /// </summary>
    public static List<(int Open, int Close)> ExtraWindows(OperationSnapshot op, IEnumerable<(int Open, int Close)>? extra = null)
    {
        var standard = (Min(op.Abertura), Min(op.Fechamento));
        return Enum.GetValues<DayOfWeek>().Select(d => DayWindow(op, d)).OfType<(int Open, int Close)>()
            .Concat(extra ?? [])
            .Where(w => w.Open < w.Close && w != standard)
            .Distinct().OrderBy(w => w.Open).ThenBy(w => w.Close).ToList();
    }

    /// <summary>
    /// Tipo do turno pela operação (ver <see cref="OperationPlan"/>); null = fora da operação (só manual). Com expedientes
    /// por dia (<see cref="ExtraWindows"/>), o turno que começa na abertura/termina no fechamento de um deles também é
    /// Abertura/Fechamento (o motor só o usa nos dias em que ele começa dentro do expediente). Com turnos automáticos
    /// (<see cref="OperationSnapshot.TurnosAutomaticos"/>) o catálogo fica fora da operação (só manual): null.
    /// </summary>
    public static string? Classify(OperationSnapshot op, TimeOnly start, TimeOnly end, IEnumerable<(int Open, int Close)>? extraWindows = null)
    {
        if (op.TurnosAutomaticos) return null;
        var s = Min(start);
        var duration = Duration(start, end);
        var e = s + duration;
        if (s == Min(op.Abertura) && e <= Min(op.Fechamento)) return JourneyProfiles.Abertura;
        if (e == Min(op.Fechamento) && s >= Min(op.Abertura)) return JourneyProfiles.Fechamento;
        if (duration == op.DuracaoJornadaMinutos && s >= EarliestFlex(op) && s <= LatestFlex(op)
            && (s - EarliestFlex(op)) % op.IncrementoFlexivelMinutos == 0)
            return JourneyProfiles.Flexivel;
        foreach (var (open, close) in ExtraWindows(op, extraWindows))
        {
            if (s == open && (e <= close || duration == op.DuracaoJornadaMinutos)) return JourneyProfiles.Abertura;
            if (e == close && s >= open) return JourneyProfiles.Fechamento;
        }
        return null;
    }

    /// <summary>Id determinístico do turno gerado (mesmo setor, horário e intervalo → mesmo id).</summary>
    public static Guid GeneratedId(Guid sectorId, int start, int end, int breakMinutes) => GeneratedId(sectorId, start, end, breakMinutes, null);

    /// <summary>
    /// Id determinístico com o tipo (<paramref name="kind"/>): abertura/fechamento de um expediente por dia que cai no mesmo
    /// horário de uma entrada flexível ganha id próprio (os dois turnos convivem: um de cada tipo). null = id de sempre.
    /// </summary>
    public static Guid GeneratedId(Guid sectorId, int start, int end, int breakMinutes, string? kind)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(
            $"escala5x2-operacao|{sectorId:N}|{start}|{end}|{breakMinutes}{(kind is null ? "" : $"|{kind}")}"));
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x30); // versão 3 (baseado em nome, MD5)
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes, bigEndian: true);
    }

    /// <summary>
    /// Turnos da fotografia com a operação: os do catálogo (e os já gerados) classificados, mais os gerados que faltam.
    /// Idempotente. Sem operação: os turnos como estão. <paramref name="defaultBreak"/>: intervalo dos turnos gerados
    /// quando a operação não define (intervalo_padrao_minutos). Expedientes por dia (horário da loja,
    /// <paramref name="extraWindows"/> = horário especial de feriado): também gera a abertura e o fechamento de cada um
    /// (fechamento só quando o expediente comporta a jornada inteira). Só para turnos PERSONALIZADOS: com
    /// <see cref="OperationSnapshot.TurnosAutomaticos"/> os turnos dependem das regras de cada dia e são montados por
    /// PlanningContext.OperationShifts (aqui a lista volta como está).
    /// </summary>
    public static List<ShiftSnapshot> Expand(OperationSnapshot? op, Guid sectorId, IReadOnlyList<ShiftSnapshot> shifts, int defaultBreak,
        IEnumerable<(int Open, int Close)>? extraWindows = null)
    {
        if (op is null || op.TurnosAutomaticos) return shifts.ToList();
        var brk = op.IntervaloMinutos ?? defaultBreak;
        var dur = op.DuracaoJornadaMinutos;
        var windows = ExtraWindows(op, extraWindows);
        // Abertura/fechamento dos expedientes por dia com id próprio (convivem com a entrada flexível de mesmo horário)
        var windowShifts = windows
            .SelectMany(w => new[]
            {
                (Id: GeneratedId(sectorId, w.Open, w.Open + dur, brk, JourneyProfiles.Abertura), Tipo: JourneyProfiles.Abertura),
                (Id: GeneratedId(sectorId, w.Close - dur, w.Close, brk, JourneyProfiles.Fechamento), Tipo: JourneyProfiles.Fechamento)
            })
            .GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.First().Tipo);
        var list = shifts.Select(s => s with { Tipo = windowShifts.GetValueOrDefault(s.ShiftId) ?? Classify(op, s.Start, s.End, windows) }).ToList();
        var codes = list.Select(s => s.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);

        void Add(int start, string prefix, string tipo, bool perDay = false)
        {
            var end = start + dur;
            var id = perDay ? GeneratedId(sectorId, start, end, brk, tipo) : GeneratedId(sectorId, start, end, brk);
            if (list.Any(s => s.ShiftId == id)) return;
            var code = $"{prefix} {Hm(start)}";
            for (var n = 2; codes.Contains(code); n++) code = $"{prefix} {Hm(start)}#{n}";
            codes.Add(code);
            list.Add(new ShiftSnapshot(id, code, Time(start), Time(end), brk, tipo, true));
        }

        bool Has(string tipo, int start) => list.Any(s => s.Tipo == tipo && Min(s.Start) == start && Duration(s.Start, s.End) == dur);

        var open = Min(op.Abertura);
        var closeStart = Min(op.Fechamento) - dur;
        if (!list.Any(s => s.Tipo == JourneyProfiles.Abertura && Duration(s.Start, s.End) == dur)) Add(open, "AB", JourneyProfiles.Abertura);
        if (!list.Any(s => s.Tipo == JourneyProfiles.Fechamento && Duration(s.Start, s.End) == dur)) Add(closeStart, "FE", JourneyProfiles.Fechamento);
        foreach (var e in FlexEntries(op))
            if (!Has(JourneyProfiles.Flexivel, e)) Add(e, "FX", JourneyProfiles.Flexivel);
        foreach (var (wOpen, wClose) in windows)
        {
            if (!Has(JourneyProfiles.Abertura, wOpen)) Add(wOpen, "AB", JourneyProfiles.Abertura, perDay: true);
            if (wClose - dur > wOpen && !Has(JourneyProfiles.Fechamento, wClose - dur)) Add(wClose - dur, "FE", JourneyProfiles.Fechamento, perDay: true);
        }
        return list.OrderBy(s => Min(s.Start)).ThenBy(s => s.Code).ToList();
    }
}

using System.Globalization;
using System.Text;

namespace Escala5x2.BuildingBlocks.Contracts.Planning;

/// <summary>
/// Abrangência do feriado: Nacional (todas as lojas), Estadual (lojas da UF), Municipal (lojas do município/UF ou do
/// código IBGE) ou Loja (uma loja específica: data comemorativa/fechamento próprio da loja).
/// </summary>
public enum HolidayScope { Nacional, Estadual, Municipal, Loja }

/// <summary>
/// Funcionamento da loja num dia: Normal (horário da semana), Fechada (sem demanda, ninguém escalado) ou Especial
/// (abre/fecha num horário próprio: a janela de cobertura do dia fica restrita a ele).
/// </summary>
public enum FuncionamentoDia { Normal, Fechada, Especial }

/// <summary>Funcionamento num feriado (padrão da loja, do próprio feriado ou exceção da loja para o feriado).</summary>
public sealed record HolidayHours(FuncionamentoDia Funcionamento, TimeOnly? Abertura = null, TimeOnly? Fechamento = null);

/// <summary>
/// Localização da loja para aplicar os feriados: cidade/UF (comparadas sem acentos/maiúsculas) e, quando houver, o código
/// IBGE do município (com IBGE nos dois lados, vale o IBGE).
/// </summary>
public sealed record StoreLocation(Guid StoreId, string? City, string? Uf, string? CodigoIbge = null);

/// <summary>
/// Feriado como cadastrado no Store.Api (data base; recorrente = repete todo ano no mesmo dia/mês a partir do ano da data).
/// <paramref name="PontoFacultativo"/>: não é feriado legal (Carnaval, Corpus Christi): o trabalho não depende de
/// convenção. <paramref name="StoreId"/>: loja do feriado de abrangência Loja. <paramref name="CodigoIbge"/>: município
/// (feriado municipal). <paramref name="Funcionamento"/>/<paramref name="Abertura"/>/<paramref name="Fechamento"/>:
/// funcionamento definido no próprio feriado (null = padrão de feriado da loja).
/// </summary>
public sealed record HolidayDefinition(
    Guid Id, HolidayScope Abrangencia, string? Uf, string? Municipio, DateOnly Date, string Name, bool Recorrente,
    bool WorkAuthorized, string? AuthorizationRef, bool PontoFacultativo = false, Guid? StoreId = null, string? CodigoIbge = null,
    FuncionamentoDia? Funcionamento = null, TimeOnly? Abertura = null, TimeOnly? Fechamento = null);

/// <summary>Um feriado num dia concreto (recorrente já expandido).</summary>
public sealed record HolidayOccurrence(HolidayDefinition Holiday, DateOnly Date);

/// <summary>Sugestão de feriado (nacional/estadual) para o ano, que o administrador confirma/importa.</summary>
public sealed record HolidaySuggestion(DateOnly Date, string Name, HolidayScope Abrangencia, string? Uf, bool Recorrente,
    bool PontoFacultativo, string? Observacao = null);

/// <summary>
/// Regras de calendário dos feriados, usadas pelo Store.Api (listagem e fotografia) e pelos testes:
/// qual feriado vale para qual loja e em quais dias. Município e UF são comparados sem diferenciar
/// maiúsculas, acentos e espaços repetidos ("Cabo de Santo Agostinho" = "CABO DE SANTO  AGOSTINHO").
/// </summary>
public static class HolidayCalendar
{
    /// <summary>Texto normalizado para comparar município/UF: sem acentos, maiúsculo, espaços simples, sem espaços nas pontas.</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var decomposed = text.Trim().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        var space = false;
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsWhiteSpace(c))
            {
                if (!space) sb.Append(' ');
                space = true;
                continue;
            }
            space = false;
            sb.Append(char.ToUpperInvariant(c));
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>
    /// Chave do local, usada para filtrar e para não duplicar: "" (nacional), "PE" (estadual),
    /// "PE|CABO DE SANTO AGOSTINHO" (municipal).
    /// </summary>
    public static string LocalKey(HolidayScope scope, string? uf, string? municipio, Guid? storeId = null) => scope switch
    {
        HolidayScope.Nacional => string.Empty,
        HolidayScope.Estadual => Normalize(uf),
        HolidayScope.Loja => $"LOJA|{storeId ?? Guid.Empty:N}",
        _ => $"{Normalize(uf)}|{Normalize(municipio)}"
    };

    /// <summary>O feriado vale para a loja da cidade/UF informadas? (Sem a loja: feriado de abrangência Loja nunca vale.)</summary>
    public static bool AppliesTo(HolidayDefinition h, string? storeCity, string? storeUf) =>
        AppliesTo(h, new StoreLocation(Guid.Empty, storeCity, storeUf));

    /// <summary>
    /// O feriado vale para a loja? Nacional: todas; Estadual: mesma UF; Municipal: mesmo código IBGE (quando o feriado e a
    /// loja têm) ou mesma UF + município; Loja: a própria loja.
    /// </summary>
    public static bool AppliesTo(HolidayDefinition h, StoreLocation loja) => h.Abrangencia switch
    {
        HolidayScope.Nacional => true,
        HolidayScope.Estadual => Normalize(h.Uf).Length > 0 && Normalize(h.Uf) == Normalize(loja.Uf),
        HolidayScope.Loja => h.StoreId is { } id && id != Guid.Empty && id == loja.StoreId,
        _ => !string.IsNullOrWhiteSpace(h.CodigoIbge) && !string.IsNullOrWhiteSpace(loja.CodigoIbge)
            ? h.CodigoIbge.Trim() == loja.CodigoIbge.Trim()
            : Normalize(h.Uf) == Normalize(loja.Uf) && Normalize(h.Municipio).Length > 0 && Normalize(h.Municipio) == Normalize(loja.City)
    };

    /// <summary>
    /// Dias do feriado entre <paramref name="from"/> e <paramref name="to"/>. Recorrente: o mesmo dia/mês em cada ano,
    /// a partir do ano da data cadastrada (29/02 só nos anos bissextos).
    /// </summary>
    public static IEnumerable<DateOnly> Occurrences(HolidayDefinition h, DateOnly from, DateOnly to)
    {
        if (!h.Recorrente)
        {
            if (h.Date >= from && h.Date <= to) yield return h.Date;
            yield break;
        }
        for (var year = Math.Max(from.Year, h.Date.Year); year <= to.Year; year++)
        {
            if (h.Date.Month == 2 && h.Date.Day == 29 && !DateTime.IsLeapYear(year)) continue;
            var date = new DateOnly(year, h.Date.Month, h.Date.Day);
            if (date >= from && date <= to) yield return date;
        }
    }

    /// <summary>Os dois feriados caem no mesmo dia (um deles recorrente: mesmo dia/mês; senão a mesma data)?</summary>
    public static bool SameDay(HolidayDefinition a, HolidayDefinition b) =>
        a.Recorrente || b.Recorrente
            ? a.Date.Month == b.Date.Month && a.Date.Day == b.Date.Day
            : a.Date == b.Date;

    /// <summary>Feriados da loja no período, por dia (ordenados por data e abrangência).</summary>
    public static List<HolidayOccurrence> ForStore(IEnumerable<HolidayDefinition> all, string? storeCity, string? storeUf, DateOnly from, DateOnly to) =>
        ForStore(all, new StoreLocation(Guid.Empty, storeCity, storeUf), from, to);

    /// <summary>Feriados da loja no período, por dia (ordenados por data e abrangência).</summary>
    public static List<HolidayOccurrence> ForStore(IEnumerable<HolidayDefinition> all, StoreLocation loja, DateOnly from, DateOnly to) =>
        all.Where(h => AppliesTo(h, loja))
            .SelectMany(h => Occurrences(h, from, to).Select(d => new HolidayOccurrence(h, d)))
            .OrderBy(o => o.Date).ThenBy(o => o.Holiday.Abrangencia).ThenBy(o => o.Holiday.Name)
            .ToList();

    /// <summary>
    /// Fotografia dos feriados da loja no horizonte: um por dia. Na mesma data, os nomes se juntam e o
    /// trabalho só é autorizado se todos autorizam (vale o mais restritivo).
    /// </summary>
    public static List<HolidaySnapshot> Snapshot(IEnumerable<HolidayDefinition> all, string? storeCity, string? storeUf, DateOnly from, DateOnly to) =>
        Snapshot(all, new StoreLocation(Guid.Empty, storeCity, storeUf), from, to);

    /// <summary>
    /// Funcionamento efetivo da loja no feriado. Ordem: exceção da loja para o feriado → funcionamento do próprio feriado →
    /// padrão de feriado da loja → Normal. Feriado legal (não facultativo) sem autorização em convenção coletiva: sempre
    /// Fechada (Lei 10.101/2000, art. 6º-A). Horário especial sem abertura/fechamento válidos vale como Normal.
    /// </summary>
    public static HolidayHours Effective(HolidayDefinition h, HolidayHours? padraoLoja = null, HolidayHours? excecaoLoja = null)
    {
        if (!h.WorkAuthorized && !h.PontoFacultativo) return new HolidayHours(FuncionamentoDia.Fechada);
        var hours = excecaoLoja
                    ?? (h.Funcionamento is { } f ? new HolidayHours(f, h.Abertura, h.Fechamento) : null)
                    ?? padraoLoja
                    ?? new HolidayHours(FuncionamentoDia.Normal);
        if (hours.Funcionamento == FuncionamentoDia.Especial
            && (hours.Abertura is not { } a || hours.Fechamento is not { } c || a >= c))
            return new HolidayHours(FuncionamentoDia.Normal);
        return hours.Funcionamento == FuncionamentoDia.Especial ? hours : new HolidayHours(hours.Funcionamento);
    }

    /// <summary>
    /// Fotografia dos feriados da loja no horizonte: um por dia, com o funcionamento efetivo (<see cref="Effective"/>).
    /// Na mesma data vale o mais restritivo: algum fechado → Fechada (trabalho não autorizado); senão horários especiais
    /// se cruzam (janela vazia → Fechada); senão Normal. <paramref name="excecoes"/>: funcionamento da loja por feriado (Id).
    /// </summary>
    public static List<HolidaySnapshot> Snapshot(IEnumerable<HolidayDefinition> all, StoreLocation loja, DateOnly from, DateOnly to,
        HolidayHours? padraoLoja = null, IReadOnlyDictionary<Guid, HolidayHours>? excecoes = null) =>
        ForStore(all, loja, from, to)
            .GroupBy(o => o.Date)
            .Select(g =>
            {
                var efetivos = g.Select(o => Effective(o.Holiday, padraoLoja, excecoes?.GetValueOrDefault(o.Holiday.Id))).ToList();
                var funcionamento = FuncionamentoDia.Normal;
                TimeOnly? abre = null, fecha = null;
                if (efetivos.Any(e => e.Funcionamento == FuncionamentoDia.Fechada))
                {
                    funcionamento = FuncionamentoDia.Fechada;
                }
                else if (efetivos.Where(e => e.Funcionamento == FuncionamentoDia.Especial).ToList() is { Count: > 0 } especiais)
                {
                    abre = especiais.Max(e => e.Abertura!.Value);
                    fecha = especiais.Min(e => e.Fechamento!.Value);
                    funcionamento = abre < fecha ? FuncionamentoDia.Especial : FuncionamentoDia.Fechada;
                    if (funcionamento == FuncionamentoDia.Fechada) (abre, fecha) = (null, null);
                }
                return new HolidaySnapshot(g.Key,
                    string.Join(" / ", g.Select(o => o.Holiday.Name).Distinct()),
                    funcionamento != FuncionamentoDia.Fechada,
                    string.Join(", ", g.Select(o => o.Holiday.Abrangencia.ToString()).Distinct()),
                    funcionamento.ToString(), abre, fecha, g.All(o => o.Holiday.PontoFacultativo));
            })
            .ToList();

    // ---------------- Feriados nacionais e estaduais (sugestões) ----------------

    /// <summary>Domingo de Páscoa (algoritmo de Meeus/Jones/Butcher, calendário gregoriano).</summary>
    public static DateOnly Easter(int year)
    {
        var a = year % 19;
        var b = year / 100;
        var c = year % 100;
        var d = b / 4;
        var e = b % 4;
        var f = (b + 8) / 25;
        var g = (b - f + 1) / 3;
        var h = (19 * a + b - d - g + 15) % 30;
        var i = c / 4;
        var k = c % 4;
        var l = (32 + 2 * e + 2 * i - h - k) % 7;
        var m = (a + 11 * h + 22 * l) / 451;
        var month = (h + l - 7 * m + 114) / 31;
        var day = (h + l - 7 * m + 114) % 31 + 1;
        return new DateOnly(year, month, day);
    }

    /// <summary>
    /// Feriados nacionais do ano: fixos (Lei 662/1949, Lei 6.802/1980, Lei 14.759/2023 — Consciência Negra), recorrentes;
    /// e os móveis pela Páscoa, sem recorrência: Carnaval (segunda e terça, ponto facultativo), Sexta-feira Santa (feriado)
    /// e Corpus Christi (ponto facultativo).
    /// </summary>
    public static List<HolidaySuggestion> NationalSuggestions(int year)
    {
        var pascoa = Easter(year);
        HolidaySuggestion Fixo(int m, int d, string nome) => new(new DateOnly(year, m, d), nome, HolidayScope.Nacional, null, true, false);
        HolidaySuggestion Movel(int dias, string nome, bool facultativo, string? obs = null) =>
            new(pascoa.AddDays(dias), nome, HolidayScope.Nacional, null, false, facultativo, obs);
        return
        [
            Fixo(1, 1, "Confraternização Universal"),
            Movel(-48, "Carnaval (segunda-feira)", true, "Ponto facultativo: o comércio funciona conforme a convenção/decisão da empresa."),
            Movel(-47, "Carnaval (terça-feira)", true, "Ponto facultativo: o comércio funciona conforme a convenção/decisão da empresa."),
            Movel(-2, "Sexta-feira Santa", false),
            Fixo(4, 21, "Tiradentes"),
            Fixo(5, 1, "Dia do Trabalho"),
            Movel(60, "Corpus Christi", true, "Ponto facultativo nacional; pode ser feriado municipal (cadastre no município)."),
            Fixo(9, 7, "Independência do Brasil"),
            Fixo(10, 12, "Nossa Senhora Aparecida"),
            Fixo(11, 2, "Finados"),
            Fixo(11, 15, "Proclamação da República"),
            Fixo(11, 20, "Dia Nacional de Zumbi e da Consciência Negra"),
            Fixo(12, 25, "Natal"),
        ];
    }

    /// <summary>
    /// Feriados estaduais conhecidos da UF (recorrentes). Pernambuco: Data Magna / Revolução Pernambucana de 1817 (6 de
    /// março) e São João (24 de junho). Conferir a lei estadual vigente antes de confirmar. Outras UFs: lista vazia
    /// (cadastrar manualmente).
    /// </summary>
    public static List<HolidaySuggestion> StateSuggestions(string? uf, int year) => Normalize(uf) switch
    {
        "PE" =>
        [
            new(new DateOnly(year, 3, 6), "Data Magna de Pernambuco (Revolução Pernambucana de 1817)", HolidayScope.Estadual, "PE", true, false,
                "Feriado estadual em 6 de março."),
            new(new DateOnly(year, 6, 24), "São João", HolidayScope.Estadual, "PE", true, false,
                "Feriado estadual em Pernambuco (confira a lei estadual vigente)."),
        ],
        _ => []
    };
}

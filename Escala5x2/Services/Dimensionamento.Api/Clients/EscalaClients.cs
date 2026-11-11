using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Escala5x2.BuildingBlocks.Contracts.Compliance;

namespace Dimensionamento.Api.Clients;

public static class Json
{
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        o.Converters.Add(new JsonStringEnumConverter());
        return o;
    }
}

/// <summary>
/// Configuração de operação da loja/setor (Store.Api, campo <c>operacao</c>). Campos ausentes = null.
/// <paramref name="TurnosAutomaticos"/>: true = turnos pelo horário da loja e pelas regras (a duração/intervalo/janela
/// flexível acima são os personalizados guardados e não valem); null = Store.Api antigo (turnos personalizados).
/// </summary>
public sealed record OperacaoLoja(
    TimeOnly? Abertura, TimeOnly? Fechamento, int? CoberturaMinima, int? MinimoAbertura, int? MinimoFechamento,
    int? IncrementoFlexivelMinutos, TimeOnly? EntradaFlexivelMaisCedo, TimeOnly? EntradaFlexivelMaisTarde,
    int? DuracaoJornadaMinutos, int? IntervaloPadraoMinutos, string Origem, bool? TurnosAutomaticos = null);

public sealed record SetorLoja(Guid Id, string Nome);

public sealed record TurnoLoja(string Codigo, TimeOnly Inicio, TimeOnly Fim, int IntervaloMinutos, bool Ativo);

/// <summary>
/// Pico manual da loja (Store.Api, campo <c>picosManuais</c>): setor (null = todos), dia da semana OU data, faixa e
/// pessoas desejadas e/ou adicional.
/// </summary>
public sealed record PicoLoja(Guid? SectorId, DayOfWeek? DiaSemana, DateOnly? Data, TimeOnly Inicio, TimeOnly Fim, int? Quantidade, int? Adicional,
    string Rotulo);

/// <summary><paramref name="AcordoJornada"/>: loja com norma coletiva (limite_diario_com_acordo_minutos), para os turnos automáticos.</summary>
public sealed record LojaInfo(Guid Id, string Codigo, string Nome, IReadOnlyList<SetorLoja> Setores, IReadOnlyList<TurnoLoja> Turnos, OperacaoLoja? Operacao,
    IReadOnlyList<PicoLoja>? Picos = null, bool AcordoJornada = false);

/// <summary>Colaborador ativo (GET /internal/planning/employees do Employee.Api). PerfilJornada quando o cadastro já tiver.</summary>
public sealed record ColaboradorInfo(Guid EmployeeId, string Name, IReadOnlyList<Guid> SectorIds, IReadOnlyList<DateOnly> UnavailableDates, string? PerfilJornada);

public sealed record CelulaEscala(Guid EmployeeId, DateOnly Date, Guid? ShiftId, TimeOnly? Start, TimeOnly? End, bool Folga, TimeOnly? BreakStart, int? BreakMinutes);

public sealed record EscalaInfo(Guid Id, Guid StoreId, Guid? SectorId, string? SectorName, string? Titulo, string Status, DateOnly StartDate, DateOnly EndDate,
    IReadOnlyList<CelulaEscala> Celulas, IReadOnlyDictionary<Guid, int> IntervaloPorTurno);

/// <summary>Resposta de outro serviço: valor ou a mensagem de indisponibilidade (o dimensionamento degrada sem ele).</summary>
public sealed record Consulta<T>(T? Valor, string? Erro) where T : class
{
    public bool Ok => Valor is not null;
}

/// <summary>
/// Chamadas aos serviços da Escala 5x2. Nunca acessa o banco deles. Store.Api (GET /api/stores/{id}) e Scheduling.Api
/// (GET /api/schedules/{id}) com o token do usuário (mesmas permissões dele); Employee.Api e Compliance.Api pelos endpoints
/// /internal (X-Internal-Key). Falha → <see cref="Consulta{T}.Erro"/> com mensagem clara, sem exceção.
/// </summary>
public sealed class EscalaClients(IHttpClientFactory factory, IHttpContextAccessor accessor, ILogger<EscalaClients> logger)
{
    public const string Employee = "employee";
    public const string Store = "store";
    public const string Compliance = "compliance";
    public const string Scheduling = "scheduling";

    private static readonly Dictionary<string, string> Nomes = new()
    {
        [Employee] = "Employee.Api (colaboradores)", [Store] = "Store.Api (lojas)",
        [Compliance] = "Compliance.Api (regras)", [Scheduling] = "Scheduling.Api (escalas)",
    };

    public async Task<Consulta<LojaInfo>> ObterLojaAsync(Guid storeId, Guid? sectorId, CancellationToken ct)
    {
        var r = await GetAsync(Store, $"/api/stores/{storeId}", comToken: true, ct);
        if (r.Erro is not null) return new(null, r.Erro);
        try
        {
            var root = r.Valor!.RootElement;
            var store = Prop(root, "store");
            var setores = Arr(root, "sectors").Select(s => new SetorLoja(
                Guid.Parse(Str(s, "sectorId") ?? Str(s, "id") ?? Guid.Empty.ToString()), Str(s, "name") ?? Str(s, "nome") ?? "Setor")).ToList();
            var turnos = Arr(root, "shifts").Select(s => new TurnoLoja(Str(s, "code") ?? "?", Hora(Str(s, "start")) ?? default, Hora(Str(s, "end")) ?? default,
                Int(s, "breakMinutes") ?? 0, Bool(s, "active") ?? true)).ToList();

            // Operação: a do setor (sectors[i].operacao ou operacoes[] com sectorId) prevalece sobre a da loja (operacao)
            OperacaoLoja? operacao = null;
            if (sectorId is { } sid)
            {
                var setor = Arr(root, "sectors").FirstOrDefault(s => Str(s, "sectorId") == sid.ToString() || Str(s, "id") == sid.ToString());
                if (setor.ValueKind == JsonValueKind.Object && Prop(setor, "operacao") is { ValueKind: JsonValueKind.Object } os)
                    operacao = Operacao(os, "operação do setor");
                operacao ??= Arr(root, "operacoes").Where(o => Str(o, "sectorId") == sid.ToString()).Select(o => Operacao(o, "operação do setor")).FirstOrDefault();
            }
            if (operacao is null && Prop(root, "operacao") is { ValueKind: JsonValueKind.Object } op) operacao = Operacao(op, "operação da loja");

            // Picos manuais (horários de pico cadastrados pelo usuário)
            var picos = Arr(root, "picosManuais")
                .Select(p => (p, Inicio: Hora(Str(p, "inicio")), Fim: Hora(Str(p, "fim"))))
                .Where(x => x.Inicio is not null && x.Fim is not null)
                .Select(x => new PicoLoja(
                    Guid.TryParse(Str(x.p, "sectorId"), out var ps) ? ps : null,
                    Enum.TryParse<DayOfWeek>(Str(x.p, "diaSemana"), true, out var dia) ? dia : null,
                    DateOnly.TryParse(Str(x.p, "data"), CultureInfo.InvariantCulture, out var data) ? data : null,
                    x.Inicio!.Value, x.Fim!.Value, Int(x.p, "quantidade"), Int(x.p, "adicional"), Str(x.p, "rotulo") ?? "Pico manual"))
                .ToList();

            return new(new LojaInfo(storeId, Str(store, "code") ?? string.Empty, Str(store, "name") ?? string.Empty, setores, turnos, operacao, picos,
                Bool(store, "hasExtendedDayAgreement") ?? false), null);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or KeyNotFoundException)
        {
            logger.LogWarning("Resposta inesperada do Store.Api para a loja {StoreId}: {Motivo}", storeId, ex.Message);
            return new(null, "O Store.Api respondeu num formato inesperado.");
        }
        finally
        {
            r.Valor?.Dispose();
        }
    }

    public async Task<Consulta<List<ColaboradorInfo>>> ObterColaboradoresAsync(Guid storeId, DateOnly de, DateOnly ate, CancellationToken ct)
    {
        var r = await GetAsync(Employee, $"/internal/planning/employees?storeId={storeId}&from={de:yyyy-MM-dd}&to={ate:yyyy-MM-dd}", comToken: false, ct);
        if (r.Erro is not null) return new(null, r.Erro);
        using var doc = r.Valor!;
        try
        {
            var lista = doc.RootElement.EnumerateArray().Select(e => new ColaboradorInfo(
                Guid.Parse(Str(e, "employeeId")!), Str(e, "name") ?? string.Empty,
                Arr(e, "sectorIds").Select(s => Guid.Parse(s.GetString()!)).ToList(),
                Arr(e, "unavailableDates").Select(s => DateOnly.Parse(s.GetString()!, CultureInfo.InvariantCulture)).ToList(),
                // O Employee.Api manda o perfil aninhado em "jornada"; o nível de cima fica como alternativa
                (e.TryGetProperty("jornada", out var jornada) && jornada.ValueKind == JsonValueKind.Object ? Str(jornada, "perfilJornada") : null)
                    ?? Str(e, "perfilJornada"))).ToList();
            return new(lista, null);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or ArgumentNullException)
        {
            logger.LogWarning("Resposta inesperada do Employee.Api: {Motivo}", ex.Message);
            return new(null, "O Employee.Api respondeu num formato inesperado.");
        }
    }

    public async Task<Consulta<List<ComplianceRuleSnapshot>>> ObterRegrasAsync(DateOnly de, DateOnly ate, CancellationToken ct)
    {
        var r = await GetAsync(Compliance, $"/internal/rules?from={de:yyyy-MM-dd}&to={ate:yyyy-MM-dd}", comToken: false, ct);
        if (r.Erro is not null) return new(null, r.Erro);
        using var doc = r.Valor!;
        try
        {
            return new(doc.Deserialize<List<ComplianceRuleSnapshot>>(Json.Options) ?? [], null);
        }
        catch (JsonException ex)
        {
            logger.LogWarning("Resposta inesperada do Compliance.Api: {Motivo}", ex.Message);
            return new(null, "O Compliance.Api respondeu num formato inesperado.");
        }
    }

    public async Task<Consulta<EscalaInfo>> ObterEscalaAsync(Guid id, CancellationToken ct)
    {
        var r = await GetAsync(Scheduling, $"/api/schedules/{id}", comToken: true, ct);
        if (r.Erro is not null) return new(null, r.Erro);
        using var doc = r.Valor!;
        try
        {
            var root = doc.RootElement;
            var s = Prop(root, "summary");
            var intervalos = Arr(root, "shifts").ToDictionary(x => Guid.Parse(Str(x, "id")!), x => Int(x, "breakMinutes") ?? 0);
            var celulas = Arr(root, "assignments").Select(a => new CelulaEscala(
                Guid.Parse(Str(a, "employeeId")!), DateOnly.Parse(Str(a, "date")!, CultureInfo.InvariantCulture),
                Guid.TryParse(Str(a, "shiftId"), out var sh) ? sh : null, Hora(Str(a, "start")), Hora(Str(a, "end")),
                Bool(a, "folga") ?? false, Hora(Str(a, "breakStart")), Int(a, "breakMinutes"))).ToList();
            return new(new EscalaInfo(id, Guid.Parse(Str(s, "storeId")!), Guid.TryParse(Str(s, "sectorId"), out var sec) ? sec : null,
                Str(s, "sectorName"), Str(s, "titulo"), Str(s, "status") ?? string.Empty,
                DateOnly.Parse(Str(s, "startDate")!, CultureInfo.InvariantCulture), DateOnly.Parse(Str(s, "endDate")!, CultureInfo.InvariantCulture),
                celulas, intervalos), null);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or ArgumentNullException or ArgumentException)
        {
            logger.LogWarning("Resposta inesperada do Scheduling.Api para a escala {Id}: {Motivo}", id, ex.Message);
            return new(null, "O Scheduling.Api respondeu num formato inesperado.");
        }
    }

    private async Task<(JsonDocument? Valor, string? Erro)> GetAsync(string cliente, string caminho, bool comToken, CancellationToken ct)
    {
        var http = factory.CreateClient(cliente);
        using var req = new HttpRequestMessage(HttpMethod.Get, caminho);
        if (comToken && AuthenticationHeaderValue.TryParse(accessor.HttpContext?.Request.Headers.Authorization.ToString(), out var auth))
            req.Headers.Authorization = auth;
        try
        {
            using var res = await http.SendAsync(req, ct);
            if (res.StatusCode == HttpStatusCode.NotFound) return (null, $"{Nomes[cliente]}: registro não encontrado.");
            if (res.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
                return (null, $"{Nomes[cliente]}: seu perfil não tem acesso a este registro.");
            if (!res.IsSuccessStatusCode) return (null, $"{Nomes[cliente]} respondeu com erro {(int)res.StatusCode}.");
            var stream = await res.Content.ReadAsStreamAsync(ct);
            return (await JsonDocument.ParseAsync(stream, cancellationToken: ct), null);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            var motivo = ex is TaskCanceledException ? $"sem resposta em {http.Timeout.TotalSeconds:0} s" : ex.Message;
            // Sem a pilha: é indisponibilidade de outro serviço, não erro deste
            logger.LogWarning("{Servico} indisponível ao consultar {Caminho} ({Motivo})", Nomes[cliente], caminho, motivo);
            return (null, $"{Nomes[cliente]} indisponível ({motivo}).");
        }
    }

    private static OperacaoLoja Operacao(JsonElement o, string origem) => new(
        Hora(Str(o, "abertura")), Hora(Str(o, "fechamento")), Int(o, "coberturaMinima"), Int(o, "minimoAbertura"), Int(o, "minimoFechamento"),
        Int(o, "incrementoFlexivelMinutos"), Hora(Str(o, "entradaFlexivelMaisCedo")), Hora(Str(o, "entradaFlexivelMaisTarde")),
        Int(o, "duracaoJornadaMinutos"), Int(o, "intervaloPadraoMinutos"), origem, Bool(o, "turnosAutomaticos"));

    private static JsonElement Prop(JsonElement e, string nome)
    {
        if (e.ValueKind != JsonValueKind.Object) return default;
        foreach (var p in e.EnumerateObject())
            if (string.Equals(p.Name, nome, StringComparison.OrdinalIgnoreCase)) return p.Value;
        return default;
    }

    private static IEnumerable<JsonElement> Arr(JsonElement e, string nome) =>
        Prop(e, nome) is { ValueKind: JsonValueKind.Array } a ? a.EnumerateArray() : [];

    private static string? Str(JsonElement e, string nome) => Prop(e, nome) switch
    {
        { ValueKind: JsonValueKind.String } s => s.GetString(),
        { ValueKind: JsonValueKind.Number } n => n.GetRawText(),
        _ => null
    };

    private static int? Int(JsonElement e, string nome) => Prop(e, nome) switch
    {
        { ValueKind: JsonValueKind.Number } n when n.TryGetDecimal(out var v) => (int)Math.Round(v),
        { ValueKind: JsonValueKind.String } s when int.TryParse(s.GetString(), CultureInfo.InvariantCulture, out var v) => v,
        _ => null
    };

    private static bool? Bool(JsonElement e, string nome) => Prop(e, nome) switch
    {
        { ValueKind: JsonValueKind.True } => true,
        { ValueKind: JsonValueKind.False } => false,
        _ => null
    };

    public static TimeOnly? Hora(string? texto) =>
        string.IsNullOrWhiteSpace(texto) ? null
        : TimeOnly.TryParse(texto, CultureInfo.InvariantCulture, out var t) ? t : null;
}

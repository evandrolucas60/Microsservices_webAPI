using System.Globalization;
using Escala5x2.BuildingBlocks.Contracts.Planning;
using Escala5x2.BuildingBlocks.Contracts.Stores;
using Escala5x2.BuildingBlocks.Shared.Auth;
using Escala5x2.BuildingBlocks.Shared.Web;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Store.Api.Data;
using Store.Api.Domain;

namespace Store.Api.Endpoints;

/// <summary>Horário de um dia da semana ("HH:mm"). <paramref name="Fechada"/>: a loja não abre.</summary>
public sealed record StoreHoursDto(DayOfWeek DiaSemana, bool Fechada, string? Abertura, string? Fechamento);

/// <summary>
/// Cadastro da loja: endereço, município/UF/IBGE (feriados estaduais e municipais), horário de funcionamento por dia da
/// semana (vazio = não cadastrado: vale o expediente da operação todos os dias) e funcionamento padrão nos feriados
/// (null = horário normal do dia; Fechada; Especial com abertura/fechamento). <paramref name="LocalizacaoCompleta"/>:
/// cidade e UF informadas (sem elas, só os feriados nacionais e os da loja valem).
/// </summary>
public sealed record StoreCadastroResponse(
    string? Logradouro, string? Numero, string? Bairro, string? Cep, string City, string State, string? CodigoIbge,
    List<StoreHoursDto> Horarios, string? FeriadoFuncionamento, string? FeriadoAbertura, string? FeriadoFechamento,
    bool LocalizacaoCompleta, string? AtualizadoPor, DateTime? AtualizadoEm);

/// <summary>Edição do cadastro da loja. <paramref name="Horarios"/>: os 7 dias, ou vazio para remover o horário cadastrado.</summary>
public sealed record StoreCadastroRequest(
    string? Logradouro, string? Numero, string? Bairro, string? Cep, string? City, string? State, string? CodigoIbge,
    List<StoreHoursDto>? Horarios, FuncionamentoDia? FeriadoFuncionamento, string? FeriadoAbertura, string? FeriadoFechamento);

/// <summary>Pico manual na resposta (horários "HH:mm"). <paramref name="Rotulo"/>: "Pico manual 11:00–14:00 de sábado".</summary>
public sealed record ManualPeakResponse(Guid Id, Guid? SectorId, string? SectorName, DayOfWeek? DiaSemana, DateOnly? Data,
    string Inicio, string Fim, int? Quantidade, int? Adicional, string? Descricao, string Rotulo, string AtualizadoPor, DateTime AtualizadoEm);

/// <summary>
/// Criação/edição de pico manual: setor opcional (vazio = todos os setores da loja); <paramref name="DiaSemana"/> OU
/// <paramref name="Data"/>; <paramref name="Quantidade"/> (pessoas desejadas na faixa) e/ou <paramref name="Adicional"/>
/// (pessoas a mais sobre a demanda/mínimo).
/// </summary>
public sealed record ManualPeakRequest(Guid? SectorId, DayOfWeek? DiaSemana, DateOnly? Data, string? Inicio, string? Fim,
    int? Quantidade, int? Adicional, string? Descricao);

/// <summary>Funcionamento da loja num feriado (exceção). Horários "HH:mm" (só Especial).</summary>
public sealed record HolidayStoreHoursRequest(FuncionamentoDia? Funcionamento, string? Abertura, string? Fechamento);

/// <summary>
/// Cadastro da loja (localização e horário de funcionamento), picos manuais e funcionamento da loja nos feriados
/// (gateway: /api/escala5x2/stores/...). Admin e Gestor da loja; o Gestor não muda cidade/UF/IBGE (definem os feriados
/// estaduais e municipais que valem para a loja e o que o gestor pode cadastrar): só o Admin.
/// <list type="bullet">
/// <item>PUT /api/stores/{id}/cadastro — endereço, localização, horários e padrão de feriados.</item>
/// <item>POST /api/stores/{id}/picos · PUT/DELETE /api/stores/{id}/picos/{peakId} — picos manuais.</item>
/// <item>PUT/DELETE /api/stores/{id}/holidays/{holidayId}/funcionamento — exceção da loja para um feriado.</item>
/// </list>
/// Cada ação publica StorePlanningChanged (auditoria). Vale para as próximas gerações de escala.
/// </summary>
public static class StoreCadastroEndpoints
{
    public static string Hm(TimeOnly t) => t.ToString("HH:mm", CultureInfo.InvariantCulture);

    public static bool TryTime(string? value, out TimeOnly? time)
    {
        time = null;
        if (string.IsNullOrWhiteSpace(value)) return true;
        if (!TimeOnly.TryParseExact(value.Trim(), ["HH:mm", "H:mm", "HH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
            return false;
        time = t;
        return true;
    }

    public static StoreCadastroResponse Cadastro(Domain.Store s, IEnumerable<StoreHours> horarios) => new(
        s.Logradouro, s.Numero, s.Bairro, s.Cep, s.City, s.State, s.CodigoIbge,
        horarios.OrderBy(h => ((int)h.DiaSemana + 6) % 7)
            .Select(h => new StoreHoursDto(h.DiaSemana, h.Fechada, h.Abertura is { } a ? Hm(a) : null, h.Fechamento is { } f ? Hm(f) : null))
            .ToList(),
        s.FeriadoFuncionamento?.ToString(), s.FeriadoAbertura is { } fa ? Hm(fa) : null, s.FeriadoFechamento is { } ff ? Hm(ff) : null,
        !string.IsNullOrWhiteSpace(s.City) && !string.IsNullOrWhiteSpace(s.State), s.CadastroAtualizadoPor, s.CadastroAtualizadoEm);

    /// <summary>Horário de funcionamento na fotografia (null = loja sem horário cadastrado).</summary>
    public static List<DayHours>? DayHoursOf(IEnumerable<StoreHours> horarios)
    {
        var list = horarios.Select(h => new DayHours(h.DiaSemana, h.Fechada, h.Abertura, h.Fechamento)).ToList();
        return list.Count == 0 ? null : list;
    }

    /// <summary>
    /// Expediente padrão do horário de funcionamento: o par abertura/fechamento mais frequente entre os dias abertos
    /// (empate: o que abre mais cedo). É o expediente da operação da loja quando há horário cadastrado. null = nenhum dia aberto.
    /// </summary>
    public static (TimeOnly Abertura, TimeOnly Fechamento)? StandardWindow(IEnumerable<StoreHours> horarios) =>
        horarios.Where(h => !h.Fechada && h.Abertura is not null && h.Fechamento is not null)
            .GroupBy(h => (h.Abertura!.Value, h.Fechamento!.Value))
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key.Item1).ThenByDescending(g => g.Key.Item2)
            .Select(g => ((TimeOnly, TimeOnly)?)g.Key).FirstOrDefault();

    public static ManualPeakResponse ToResponse(ManualPeak p, IReadOnlyDictionary<Guid, string> sectorNames) => new(
        p.Id, p.SectorId, p.SectorId is { } sid ? sectorNames.GetValueOrDefault(sid) : null, p.DiaSemana, p.Data, Hm(p.Inicio), Hm(p.Fim),
        p.Quantidade, p.Adicional, p.Descricao, ManualPeaks.Label(p.Inicio, p.Fim, p.Dia, p.Data), p.AtualizadoPor, p.AtualizadoEm);

    /// <summary>Linhas de demanda da fotografia para os picos manuais (pico da loja inteira: uma linha por setor).</summary>
    public static IEnumerable<DemandSnapshot> PeakRows(IEnumerable<ManualPeak> peaks, IEnumerable<Sector> sectors)
    {
        var ids = sectors.Select(s => s.Id).ToList();
        foreach (var p in peaks)
        {
            var label = ManualPeaks.Label(p.Inicio, p.Fim, p.Dia, p.Data) + (string.IsNullOrWhiteSpace(p.Descricao) ? "" : $" ({p.Descricao})");
            foreach (var sid in p.SectorId is { } only ? [only] : ids)
                yield return new DemandSnapshot(sid, p.Dia, p.Inicio, p.Fim, p.Quantidade ?? 0, true, p.Data, p.Adicional, label);
        }
    }

    private static string DescribeHours(IEnumerable<StoreHours> horarios) =>
        !horarios.Any() ? "sem horário cadastrado" : string.Join(", ", horarios.OrderBy(h => ((int)h.DiaSemana + 6) % 7).Select(h =>
            $"{ManualPeaks.DayName(h.DiaSemana)[..3]} {(h.Fechada ? "fechada" : $"{(h.Abertura is { } a ? Hm(a) : "?")}–{(h.Fechamento is { } f ? Hm(f) : "?")}")}"));

    private static string DescribeHoliday(FuncionamentoDia? f, TimeOnly? a, TimeOnly? c) => f switch
    {
        null => "feriado: horário normal",
        FuncionamentoDia.Especial => $"feriado: {(a is { } x ? Hm(x) : "?")}–{(c is { } y ? Hm(y) : "?")}",
        _ => $"feriado: {f.ToString()!.ToLowerInvariant()}"
    };

    /// <summary>Valida um horário especial (abertura antes do fechamento, no mesmo dia).</summary>
    private static IResult? ValidateSpecial(string campo, string? abertura, string? fechamento, out TimeOnly? a, out TimeOnly? f)
    {
        f = null;
        if (!TryTime(abertura, out a) || a is null) return ServiceDefaults.ValidationError($"{campo}Abertura", "Informe a abertura do horário especial (HH:mm).");
        if (!TryTime(fechamento, out f) || f is null) return ServiceDefaults.ValidationError($"{campo}Fechamento", "Informe o fechamento do horário especial (HH:mm).");
        if (a >= f) return ServiceDefaults.ValidationError($"{campo}Fechamento", "A abertura precisa ser antes do fechamento (no mesmo dia).");
        return null;
    }

    private static async ValueTask<object?> OwnStoreOnly(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var user = ctx.HttpContext.RequestServices.GetRequiredService<ICurrentUser>();
        if (Guid.TryParse(ctx.HttpContext.Request.RouteValues["id"]?.ToString(), out var storeId) && !user.CanManageStore(storeId))
            return Results.Forbid();
        return await next(ctx);
    }

    public static void MapStoreCadastroEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/stores").RequireAuthorization();

        // ---------- Cadastro da loja ----------
        group.MapPut("/{id:guid}/cadastro", async (Guid id, StoreCadastroRequest req, StoreDbContext db, IPublishEndpoint bus, ICurrentUser user) =>
        {
            var store = await db.Stores.Include(s => s.Horarios).FirstOrDefaultAsync(s => s.Id == id);
            if (store is null) return Results.NotFound();

            static string? Text(string? v, int max)
            {
                var t = string.Join(' ', (v ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries));
                return t.Length == 0 ? null : t[..Math.Min(max, t.Length)];
            }
            var city = string.Join(' ', (req.City ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries));
            var state = req.State?.Trim().ToUpperInvariant() ?? string.Empty;
            var cep = new string((req.Cep ?? string.Empty).Where(char.IsDigit).ToArray());
            var ibge = new string((req.CodigoIbge ?? string.Empty).Where(char.IsDigit).ToArray());
            if (city.Length > 120) return ServiceDefaults.ValidationError("city", "O município tem no máximo 120 caracteres.");
            if (state.Length != 2 || !state.All(char.IsLetter)) return ServiceDefaults.ValidationError("state", "Informe a UF (sigla).");
            if (cep.Length is not (0 or 8)) return ServiceDefaults.ValidationError("cep", "O CEP tem 8 dígitos.");
            if (ibge.Length is not (0 or 7)) return ServiceDefaults.ValidationError("codigoIbge", "O código IBGE do município tem 7 dígitos.");
            if (ibge.Length == 7 && ibge[..2] != UfIbge(state))
                return ServiceDefaults.ValidationError("codigoIbge", $"O código IBGE {ibge} não é de um município de {state} (começa com {UfIbge(state) ?? "?"}).");
            // Localização define os feriados estaduais/municipais da loja e o que o gestor cadastra: só o Admin muda
            var mudouLocal = HolidayCalendar.Normalize(city) != HolidayCalendar.Normalize(store.City) || state != store.State
                             || (ibge.Length == 0 ? null : ibge) != store.CodigoIbge;
            if (mudouLocal && user.Role != Roles.Admin)
                return Results.Problem("Município, UF e código IBGE da loja são alterados só pelo administrador (definem os feriados da loja).",
                    statusCode: StatusCodes.Status403Forbidden);

            // Horário por dia: os 7 dias (ou nenhum, para voltar ao expediente da operação)
            var horarios = new List<StoreHours>();
            if (req.Horarios is { Count: > 0 } hs)
            {
                if (hs.Count != 7 || hs.Select(h => h.DiaSemana).Distinct().Count() != 7 || hs.Any(h => !Enum.IsDefined(h.DiaSemana)))
                    return ServiceDefaults.ValidationError("horarios", "Informe o horário dos 7 dias da semana (ou marque o dia como fechado).");
                foreach (var h in hs)
                {
                    var dia = ManualPeaks.DayName(h.DiaSemana);
                    if (h.Fechada)
                    {
                        horarios.Add(new StoreHours { StoreId = id, DiaSemana = h.DiaSemana, Fechada = true });
                        continue;
                    }
                    if (!TryTime(h.Abertura, out var a) || a is null) return ServiceDefaults.ValidationError("horarios", $"Informe a abertura de {dia} (HH:mm).");
                    if (!TryTime(h.Fechamento, out var f) || f is null) return ServiceDefaults.ValidationError("horarios", $"Informe o fechamento de {dia} (HH:mm).");
                    if (a >= f) return ServiceDefaults.ValidationError("horarios", $"Em {dia}, a abertura precisa ser antes do fechamento (o expediente não vira o dia).");
                    horarios.Add(new StoreHours { StoreId = id, DiaSemana = h.DiaSemana, Abertura = a, Fechamento = f });
                }
                if (horarios.All(h => h.Fechada)) return ServiceDefaults.ValidationError("horarios", "A loja precisa abrir em pelo menos um dia da semana.");
            }

            // Funcionamento padrão nos feriados
            TimeOnly? feriadoAbre = null, feriadoFecha = null;
            if (req.FeriadoFuncionamento is { } ff && !Enum.IsDefined(ff))
                return ServiceDefaults.ValidationError("feriadoFuncionamento", "Funcionamento inválido: Normal, Fechada ou Especial.");
            if (req.FeriadoFuncionamento == FuncionamentoDia.Especial
                && ValidateSpecial("feriado", req.FeriadoAbertura, req.FeriadoFechamento, out feriadoAbre, out feriadoFecha) is { } bad)
                return bad;

            // O horário é a fonte do expediente da operação da loja: a abertura/fechamento padrão passa a ser o do horário
            var op = await db.Operations.FirstOrDefaultAsync(o => o.StoreId == id && o.SectorId == null);
            string? opNote = null;
            if (op is not null && StandardWindow(horarios) is { } w && (op.Abertura, op.Fechamento) != w)
            {
                var candidate = op.ToSnapshot() with { Abertura = w.Abertura, Fechamento = w.Fechamento };
                var errors = OperationPlan.Validate(candidate);
                if (errors.Count > 0)
                    return ServiceDefaults.ValidationError("horarios",
                        $"Com o expediente {Hm(w.Abertura)}–{Hm(w.Fechamento)}, a operação da loja fica inválida: {errors[0].Mensagem} Ajuste a operação ou o horário.");
                opNote = $"; expediente padrão da operação {Hm(op.Abertura)}–{Hm(op.Fechamento)} → {Hm(w.Abertura)}–{Hm(w.Fechamento)}";
                op.Abertura = w.Abertura;
                op.Fechamento = w.Fechamento;
                op.AtualizadoPor = user.Display;
                op.AtualizadoEm = DateTime.UtcNow;
            }

            var before = $"{store.Logradouro}, {store.Numero}, {store.Bairro}, {store.Cep}, {(store.City.Length > 0 ? store.City : "sem cidade")}/{store.State}" +
                         $"{(store.CodigoIbge is null ? "" : $" (IBGE {store.CodigoIbge})")}; {DescribeHours(store.Horarios)}; " +
                         DescribeHoliday(store.FeriadoFuncionamento, store.FeriadoAbertura, store.FeriadoFechamento);
            store.Logradouro = Text(req.Logradouro, 200);
            store.Numero = Text(req.Numero, 20);
            store.Bairro = Text(req.Bairro, 100);
            store.Cep = cep.Length == 0 ? null : cep;
            store.City = city;
            store.State = state;
            store.CodigoIbge = ibge.Length == 0 ? null : ibge;
            store.FeriadoFuncionamento = req.FeriadoFuncionamento is FuncionamentoDia.Normal ? null : req.FeriadoFuncionamento;
            store.FeriadoAbertura = req.FeriadoFuncionamento == FuncionamentoDia.Especial ? feriadoAbre : null;
            store.FeriadoFechamento = req.FeriadoFuncionamento == FuncionamentoDia.Especial ? feriadoFecha : null;
            db.StoreHours.RemoveRange(store.Horarios);
            db.StoreHours.AddRange(horarios);
            store.CadastroAtualizadoPor = user.Display;
            store.CadastroAtualizadoEm = DateTime.UtcNow;
            var after = $"{store.Logradouro}, {store.Numero}, {store.Bairro}, {store.Cep}, {(store.City.Length > 0 ? store.City : "sem cidade")}/{store.State}" +
                        $"{(store.CodigoIbge is null ? "" : $" (IBGE {store.CodigoIbge})")}; {DescribeHours(horarios)}; " +
                        DescribeHoliday(store.FeriadoFuncionamento, store.FeriadoAbertura, store.FeriadoFechamento);
            await db.SaveChangesAsync();
            if (after != before || opNote is not null)
                await bus.Publish(new StorePlanningChanged(id, $"Cadastro da loja {store.Code} alterado: {after}{opNote} (antes: {before})", user.Display, DateTime.UtcNow));
            return Results.Ok(Cadastro(store, horarios));
        }).RequireAuthorization(Policies.Manager).AddEndpointFilter(OwnStoreOnly);

        // ---------- Picos manuais ----------
        group.MapPost("/{id:guid}/picos", (Guid id, ManualPeakRequest req, StoreDbContext db, IPublishEndpoint bus, ICurrentUser user) =>
                SavePeakAsync(id, null, req, db, bus, user))
            .RequireAuthorization(Policies.Manager).AddEndpointFilter(OwnStoreOnly);
        group.MapPut("/{id:guid}/picos/{peakId:guid}", (Guid id, Guid peakId, ManualPeakRequest req, StoreDbContext db, IPublishEndpoint bus, ICurrentUser user) =>
                SavePeakAsync(id, peakId, req, db, bus, user))
            .RequireAuthorization(Policies.Manager).AddEndpointFilter(OwnStoreOnly);
        group.MapDelete("/{id:guid}/picos/{peakId:guid}", async (Guid id, Guid peakId, StoreDbContext db, IPublishEndpoint bus, ICurrentUser user) =>
        {
            var p = await db.ManualPeaks.FirstOrDefaultAsync(x => x.Id == peakId && x.StoreId == id);
            if (p is null) return Results.NotFound();
            db.ManualPeaks.Remove(p);
            await db.SaveChangesAsync();
            await bus.Publish(new StorePlanningChanged(id, $"{ManualPeaks.Label(p.Inicio, p.Fim, p.Dia, p.Data)} excluído", user.Display, DateTime.UtcNow));
            return Results.NoContent();
        }).RequireAuthorization(Policies.Manager).AddEndpointFilter(OwnStoreOnly);

        // ---------- Funcionamento da loja num feriado (exceção) ----------
        group.MapPut("/{id:guid}/holidays/{holidayId:guid}/funcionamento", async (Guid id, Guid holidayId, HolidayStoreHoursRequest req,
            StoreDbContext db, IPublishEndpoint bus, ICurrentUser user) =>
        {
            var store = await db.Stores.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id);
            var h = await db.Holidays.AsNoTracking().FirstOrDefaultAsync(x => x.Id == holidayId);
            if (store is null || h is null) return Results.NotFound();
            if (!HolidayCalendar.AppliesTo(h.ToDefinition(), store.Location()))
                return ServiceDefaults.ValidationError("holidayId", $"O feriado {h.Name} não vale para a loja {store.Code}.");
            if (req.Funcionamento is not { } f || !Enum.IsDefined(f))
                return ServiceDefaults.ValidationError("funcionamento", "Informe o funcionamento: Normal, Fechada ou Especial.");
            if (f != FuncionamentoDia.Fechada && !h.WorkAuthorized && !h.PontoFacultativo)
                return ServiceDefaults.ValidationError("funcionamento",
                    $"{h.Name} é feriado sem autorização de trabalho em convenção coletiva: a loja fica fechada (Lei 10.101/2000, art. 6º-A).");
            TimeOnly? a = null, c = null;
            if (f == FuncionamentoDia.Especial && ValidateSpecial("", req.Abertura, req.Fechamento, out a, out c) is { } bad) return bad;
            var entity = await db.HolidayStoreHours.FirstOrDefaultAsync(x => x.HolidayId == holidayId && x.StoreId == id);
            if (entity is null) db.HolidayStoreHours.Add(entity = new HolidayStoreHours { HolidayId = holidayId, StoreId = id });
            entity.Funcionamento = f;
            entity.Abertura = a;
            entity.Fechamento = c;
            entity.AtualizadoPor = user.Display;
            entity.AtualizadoEm = DateTime.UtcNow;
            await db.SaveChangesAsync();
            await bus.Publish(new StorePlanningChanged(id, $"Funcionamento da loja {store.Code} no feriado {h.Name}: {DescribeHoliday(f, a, c)[9..]}",
                user.Display, DateTime.UtcNow));
            return Results.Ok(new { funcionamento = f.ToString(), abertura = a is { } x ? Hm(x) : null, fechamento = c is { } y ? Hm(y) : null });
        }).RequireAuthorization(Policies.Manager).AddEndpointFilter(OwnStoreOnly);

        group.MapDelete("/{id:guid}/holidays/{holidayId:guid}/funcionamento", async (Guid id, Guid holidayId, StoreDbContext db,
            IPublishEndpoint bus, ICurrentUser user) =>
        {
            var entity = await db.HolidayStoreHours.FirstOrDefaultAsync(x => x.HolidayId == holidayId && x.StoreId == id);
            if (entity is null) return Results.NotFound();
            db.HolidayStoreHours.Remove(entity);
            await db.SaveChangesAsync();
            await bus.Publish(new StorePlanningChanged(id, "Exceção de funcionamento no feriado removida (volta ao padrão)", user.Display, DateTime.UtcNow));
            return Results.NoContent();
        }).RequireAuthorization(Policies.Manager).AddEndpointFilter(OwnStoreOnly);
    }

    /// <summary>Prefixo IBGE da UF (2 primeiros dígitos do código do município).</summary>
    public static string? UfIbge(string uf) => uf switch
    {
        "RO" => "11", "AC" => "12", "AM" => "13", "RR" => "14", "PA" => "15", "AP" => "16", "TO" => "17",
        "MA" => "21", "PI" => "22", "CE" => "23", "RN" => "24", "PB" => "25", "PE" => "26", "AL" => "27", "SE" => "28", "BA" => "29",
        "MG" => "31", "ES" => "32", "RJ" => "33", "SP" => "35", "PR" => "41", "SC" => "42", "RS" => "43",
        "MS" => "50", "MT" => "51", "GO" => "52", "DF" => "53",
        _ => null
    };

    /// <summary>Validação do pico manual (erro 400 ou null).</summary>
    public static IResult? ValidatePeak(ManualPeakRequest req, out TimeOnly inicio, out TimeOnly fim)
    {
        inicio = fim = default;
        if (!TryTime(req.Inicio, out var i) || i is null) return ServiceDefaults.ValidationError("inicio", "Informe o início do pico (HH:mm).");
        if (!TryTime(req.Fim, out var f) || f is null) return ServiceDefaults.ValidationError("fim", "Informe o fim do pico (HH:mm).");
        if (i >= f) return ServiceDefaults.ValidationError("fim", "O fim do pico precisa ser depois do início (no mesmo dia).");
        if ((req.DiaSemana is null) == (req.Data is null))
            return ServiceDefaults.ValidationError("diaSemana", "Informe o dia da semana OU uma data específica.");
        if (req.DiaSemana is { } d && !Enum.IsDefined(d)) return ServiceDefaults.ValidationError("diaSemana", "Dia da semana inválido.");
        if (req.Quantidade is null && req.Adicional is null)
            return ServiceDefaults.ValidationError("quantidade", "Informe as pessoas desejadas no pico e/ou o adicional.");
        if (req.Quantidade is < 1 or > 200) return ServiceDefaults.ValidationError("quantidade", "Pessoas no pico: de 1 a 200.");
        if (req.Adicional is < 1 or > 100) return ServiceDefaults.ValidationError("adicional", "Adicional: de 1 a 100 pessoas.");
        if (req.Descricao?.Trim().Length > 150) return ServiceDefaults.ValidationError("descricao", "A descrição tem no máximo 150 caracteres.");
        (inicio, fim) = (i.Value, f.Value);
        return null;
    }

    private static async Task<IResult> SavePeakAsync(Guid id, Guid? peakId, ManualPeakRequest req, StoreDbContext db, IPublishEndpoint bus, ICurrentUser user)
    {
        if (!await db.Stores.AnyAsync(s => s.Id == id)) return Results.NotFound();
        if (ValidatePeak(req, out var inicio, out var fim) is { } invalid) return invalid;
        var sectors = await db.Sectors.AsNoTracking().Where(s => s.StoreId == id).ToListAsync();
        if (req.SectorId is { } sid)
        {
            var sector = sectors.FirstOrDefault(s => s.Id == sid);
            if (sector is null) return ServiceDefaults.ValidationError("sectorId", "Setor não pertence a esta loja.");
            if (!sector.Active) return ServiceDefaults.ValidationError("sectorId", $"O setor {sector.Name} está inativo na LG.");
        }
        ManualPeak? p;
        string? before = null;
        if (peakId is { } pid)
        {
            p = await db.ManualPeaks.FirstOrDefaultAsync(x => x.Id == pid && x.StoreId == id);
            if (p is null) return Results.NotFound();
            before = ManualPeaks.Label(p.Inicio, p.Fim, p.Dia, p.Data) + $": {p.Quantidade?.ToString() ?? "—"} pessoa(s), adicional {p.Adicional?.ToString() ?? "—"}";
        }
        else
        {
            db.ManualPeaks.Add(p = new ManualPeak { StoreId = id });
        }
        p.SectorId = req.SectorId;
        p.DiaSemana = req.Data is null ? req.DiaSemana : null;
        p.Data = req.Data;
        p.Inicio = inicio;
        p.Fim = fim;
        p.Quantidade = req.Quantidade;
        p.Adicional = req.Adicional;
        p.Descricao = string.IsNullOrWhiteSpace(req.Descricao) ? null : req.Descricao.Trim();
        p.AtualizadoPor = user.Display;
        p.AtualizadoEm = DateTime.UtcNow;
        await db.SaveChangesAsync();
        var names = sectors.ToDictionary(s => s.Id, s => s.Name);
        var after = ManualPeaks.Label(p.Inicio, p.Fim, p.Dia, p.Data) +
                    $" ({(p.SectorId is { } s2 ? names.GetValueOrDefault(s2) : "todos os setores")}): {p.Quantidade?.ToString() ?? "—"} pessoa(s), adicional {p.Adicional?.ToString() ?? "—"}";
        await bus.Publish(new StorePlanningChanged(id, before is null ? $"{after} cadastrado" : $"{after} (antes: {before})", user.Display, DateTime.UtcNow));
        return Results.Ok(ToResponse(p, names));
    }
}

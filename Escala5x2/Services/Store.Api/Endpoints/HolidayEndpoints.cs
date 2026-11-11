using Escala5x2.BuildingBlocks.Contracts.Planning;
using Escala5x2.BuildingBlocks.Contracts.Stores;
using Escala5x2.BuildingBlocks.Shared.Auth;
using Escala5x2.BuildingBlocks.Shared.Web;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Store.Api.Data;
using Store.Api.Domain;

namespace Store.Api.Endpoints;

/// <summary>
/// Corpo de criação/edição de feriado. Uf: estadual e municipal; Municipio (e CodigoIbge, opcional): municipal; StoreId:
/// abrangência Loja. <paramref name="PontoFacultativo"/>: não é feriado legal (o trabalho não depende de convenção).
/// <paramref name="Funcionamento"/>: como as lojas funcionam no dia (null = padrão de feriados de cada loja); Especial
/// com <paramref name="Abertura"/>/<paramref name="Fechamento"/> ("HH:mm").
/// </summary>
public sealed record HolidayRequest(
    DateOnly? Date, string? Name, HolidayScope? Abrangencia, string? Uf, string? Municipio, bool Recorrente,
    bool WorkAuthorized, string? AuthorizationRef, bool PontoFacultativo = false, Guid? StoreId = null, string? CodigoIbge = null,
    FuncionamentoDia? Funcionamento = null, string? Abertura = null, string? Fechamento = null);

/// <summary>
/// Feriado na resposta. <paramref name="Date"/>: o dia do feriado no ano consultado (recorrente expandido) ou a data
/// cadastrada quando não há ano; <paramref name="DataBase"/>: a data cadastrada. <paramref name="PodeEditar"/>: o
/// usuário pode alterar/excluir (Admin: todos; Gestor: municipais do município da sua loja e os da sua loja).
/// Na listagem de uma loja: <paramref name="FuncionamentoEfetivo"/> (Normal | Fechada | Especial, com
/// <paramref name="AberturaEfetiva"/>/<paramref name="FechamentoEfetivo"/>) é o que o motor usa;
/// <paramref name="FuncionamentoLoja"/>: exceção da loja para o feriado (null = sem exceção).
/// </summary>
public sealed record HolidayResponse(
    Guid Id, HolidayScope Abrangencia, string? Uf, string? Municipio, DateOnly Date, DateOnly DataBase, string Name,
    bool Recorrente, bool WorkAuthorized, string? AuthorizationRef, bool PodeEditar,
    string CriadoPor, DateTime CriadoEm, string? AtualizadoPor, DateTime? AtualizadoEm,
    bool PontoFacultativo = false, Guid? StoreId = null, string? StoreName = null, string? CodigoIbge = null,
    string? Funcionamento = null, string? Abertura = null, string? Fechamento = null,
    string? FuncionamentoEfetivo = null, string? AberturaEfetiva = null, string? FechamentoEfetivo = null,
    string? FuncionamentoLoja = null);

/// <summary>Sugestão de feriado na resposta; <paramref name="JaCadastrado"/>: já existe feriado no dia para o local.</summary>
public sealed record HolidaySuggestionResponse(DateOnly Date, string Name, HolidayScope Abrangencia, string? Uf, bool Recorrente,
    bool PontoFacultativo, string? Observacao, bool JaCadastrado);

/// <summary>Importação de sugestões confirmadas pelo Admin.</summary>
public sealed record HolidayImportRequest(List<HolidayRequest>? Itens);

/// <summary>
/// Feriados nacionais, estaduais, municipais e da loja (gateway: /api/escala5x2/holidays e /api/escala5x2/stores/{id}/holidays).
/// <list type="bullet">
/// <item>GET /api/stores/{id}/holidays?ano= — feriados que valem para a loja no ano (nacionais + estaduais da UF +
/// municipais do município/IBGE + os da loja), com a abrangência e o funcionamento efetivo da loja no dia. Qualquer
/// usuário com acesso à loja.</item>
/// <item>GET /api/holidays?abrangencia=&amp;uf=&amp;municipio=&amp;ano= — todos os feriados (Admin).</item>
/// <item>GET /api/holidays/sugestoes?ano=&amp;uf= — feriados nacionais (fixos + móveis pela Páscoa) e estaduais conhecidos
/// do ano, marcando os já cadastrados (Admin). POST /api/holidays/importar — cadastra as sugestões confirmadas.</item>
/// <item>GET /api/holidays/{id} — um feriado (qualquer usuário logado).</item>
/// <item>POST /api/holidays · PUT /api/holidays/{id} · DELETE /api/holidays/{id} — Nacional e Estadual só Admin;
/// Municipal: Admin ou Gestor do município de uma loja dele; Loja: Admin ou Gestor da loja. Cada ação publica
/// HolidayChanged (auditoria).</item>
/// </list>
/// </summary>
public static class HolidayEndpoints
{
    private static readonly HashSet<string> Ufs =
    [
        "AC", "AL", "AP", "AM", "BA", "CE", "DF", "ES", "GO", "MA", "MT", "MS", "MG", "PA", "PB", "PR", "PE", "PI",
        "RJ", "RN", "RS", "RO", "RR", "SC", "SP", "SE", "TO"
    ];

    private static string? Hm(TimeOnly? t) => t is { } x ? StoreCadastroEndpoints.Hm(x) : null;

    private static HolidayResponse ToResponse(Holiday h, DateOnly date, bool canEdit, string? storeName = null) => new(
        h.Id, h.Abrangencia, h.Uf, h.Municipio, date, h.Date, h.Name, h.Recorrente, h.WorkAuthorized, h.AuthorizationRef, canEdit,
        h.CriadoPor, h.CriadoEm, h.AtualizadoPor, h.AtualizadoEm, h.PontoFacultativo, h.StoreId, storeName, h.CodigoIbge,
        h.Funcionamento?.ToString(), Hm(h.Abertura), Hm(h.Fechamento));

    private static string Resumo(Holiday h) =>
        $"{h.Date:dd/MM/yyyy} {h.Name}, {h.Abrangencia}{(h.Uf is null ? "" : $" {h.Uf}")}{(h.Municipio is null ? "" : $"/{h.Municipio}")}" +
        $"{(h.CodigoIbge is null ? "" : $" (IBGE {h.CodigoIbge})")}{(h.StoreId is null ? "" : $" loja {h.StoreId}")}" +
        $"{(h.Recorrente ? ", recorrente" : "")}{(h.PontoFacultativo ? ", ponto facultativo" : "")}" +
        $"{(h.WorkAuthorized ? $", trabalho autorizado ({h.AuthorizationRef})" : "")}" +
        $"{(h.Funcionamento is { } f ? $", funcionamento {f}{(f == FuncionamentoDia.Especial ? $" {Hm(h.Abertura)}–{Hm(h.Fechamento)}" : "")}" : "")}";

    /// <summary>
    /// Locais (chave do local) que o usuário pode gerenciar: Admin = null (todos); Gestor = municípios das lojas em
    /// que é Gestor (ICurrentUser.CanManageStore: a do perfil e as das roles de loja + cidade/UF da loja) e as próprias
    /// lojas (abrangência Loja); demais = nenhum.
    /// </summary>
    private static async Task<HashSet<string>?> ManagedLocalsAsync(ICurrentUser user, StoreDbContext db, CancellationToken ct)
    {
        if (user.Role == Roles.Admin) return null;
        if (user.Role != Roles.Gestor) return [];
        var stores = await db.Stores.AsNoTracking().Select(s => new { s.Id, s.City, s.State }).ToListAsync(ct);
        var mine = stores.Where(s => user.CanManageStore(s.Id)).ToList();
        return mine.Where(s => !string.IsNullOrWhiteSpace(s.City))
            .Select(s => HolidayCalendar.LocalKey(HolidayScope.Municipal, s.State, s.City))
            .Concat(mine.Select(s => HolidayCalendar.LocalKey(HolidayScope.Loja, null, null, s.Id)))
            .ToHashSet();
    }

    private static bool CanEdit(HashSet<string>? managed, HolidayScope scope, string localKey) =>
        managed is null || (scope is HolidayScope.Municipal or HolidayScope.Loja && managed.Contains(localKey));

    private static IResult ForbiddenScope(ICurrentUser user, HolidayScope scope) => user.Role == Roles.Gestor
        ? Results.Problem(scope switch
            {
                HolidayScope.Municipal => "O gestor só cadastra feriados municipais do município de uma loja sua.",
                HolidayScope.Loja => "O gestor só cadastra feriados de uma loja sua.",
                _ => "Feriados nacionais e estaduais são cadastrados só pelo administrador."
            },
            statusCode: StatusCodes.Status403Forbidden)
        : Results.Forbid();

    /// <summary>Valores normalizados do corpo (depois de <see cref="Validate"/>).</summary>
    private sealed record Normalized(HolidayScope Scope, string? Uf, string? Municipio, Guid? StoreId, string? Ibge, TimeOnly? Abertura, TimeOnly? Fechamento);

    /// <summary>Valida e normaliza o corpo; devolve o erro de validação (400) ou null.</summary>
    private static IResult? Validate(HolidayRequest req, out Normalized n)
    {
        var scope = req.Abrangencia ?? HolidayScope.Nacional;
        n = new Normalized(scope, null, null, null, null, null, null);
        string? uf = null, municipio = null, ibge = null;
        if (req.Date is null || req.Date == default) return ServiceDefaults.ValidationError("date", "Informe a data do feriado.");
        if (string.IsNullOrWhiteSpace(req.Name)) return ServiceDefaults.ValidationError("name", "Informe o nome do feriado.");
        if (req.Name.Trim().Length > 150) return ServiceDefaults.ValidationError("name", "O nome tem no máximo 150 caracteres.");
        if (req.Abrangencia is null || !Enum.IsDefined(scope))
            return ServiceDefaults.ValidationError("abrangencia", "Informe a abrangência: Nacional, Estadual, Municipal ou Loja.");
        if (scope is HolidayScope.Estadual or HolidayScope.Municipal)
        {
            uf = req.Uf?.Trim().ToUpperInvariant();
            if (string.IsNullOrEmpty(uf) || !Ufs.Contains(uf))
                return ServiceDefaults.ValidationError("uf", $"Informe a UF (sigla) do feriado {(scope == HolidayScope.Estadual ? "estadual" : "municipal")}.");
        }
        if (scope == HolidayScope.Municipal)
        {
            municipio = string.Join(' ', (req.Municipio ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            if (municipio.Length == 0) return ServiceDefaults.ValidationError("municipio", "Informe o município do feriado municipal.");
            if (municipio.Length > 120) return ServiceDefaults.ValidationError("municipio", "O município tem no máximo 120 caracteres.");
            ibge = new string((req.CodigoIbge ?? string.Empty).Where(char.IsDigit).ToArray());
            if (ibge.Length is not (0 or 7)) return ServiceDefaults.ValidationError("codigoIbge", "O código IBGE do município tem 7 dígitos.");
            if (ibge.Length == 7 && ibge[..2] != StoreCadastroEndpoints.UfIbge(uf!))
                return ServiceDefaults.ValidationError("codigoIbge", $"O código IBGE {ibge} não é de um município de {uf}.");
            if (ibge.Length == 0) ibge = null;
        }
        if (scope == HolidayScope.Loja && (req.StoreId is null || req.StoreId == Guid.Empty))
            return ServiceDefaults.ValidationError("storeId", "Informe a loja do feriado.");
        if (req.WorkAuthorized && string.IsNullOrWhiteSpace(req.AuthorizationRef))
            return ServiceDefaults.ValidationError("authorizationRef", "Informe a convenção coletiva que autoriza o trabalho no feriado.");
        if (req.AuthorizationRef?.Trim().Length > 200) return ServiceDefaults.ValidationError("authorizationRef", "A referência tem no máximo 200 caracteres.");
        TimeOnly? abre = null, fecha = null;
        if (req.Funcionamento is { } f)
        {
            if (!Enum.IsDefined(f)) return ServiceDefaults.ValidationError("funcionamento", "Funcionamento inválido: Normal, Fechada ou Especial.");
            if (f != FuncionamentoDia.Fechada && !req.WorkAuthorized && !req.PontoFacultativo)
                return ServiceDefaults.ValidationError("funcionamento",
                    "Para as lojas funcionarem no feriado, informe a convenção coletiva que autoriza o trabalho (ou marque ponto facultativo).");
            if (f == FuncionamentoDia.Especial)
            {
                if (!StoreCadastroEndpoints.TryTime(req.Abertura, out abre) || abre is null)
                    return ServiceDefaults.ValidationError("abertura", "Informe a abertura do horário especial (HH:mm).");
                if (!StoreCadastroEndpoints.TryTime(req.Fechamento, out fecha) || fecha is null)
                    return ServiceDefaults.ValidationError("fechamento", "Informe o fechamento do horário especial (HH:mm).");
                if (abre >= fecha) return ServiceDefaults.ValidationError("fechamento", "A abertura precisa ser antes do fechamento (no mesmo dia).");
            }
        }
        n = new Normalized(scope, uf, municipio, scope == HolidayScope.Loja ? req.StoreId : null, ibge, abre, fecha);
        return null;
    }

    /// <summary>Mesma data (ou mesmo dia/mês, se um deles é recorrente) + mesma abrangência + mesmo local = duplicado.</summary>
    private static async Task<IResult?> DuplicateAsync(StoreDbContext db, Holiday candidate, CancellationToken ct)
    {
        var dup = await FindDuplicateAsync(db, candidate, ct);
        return dup is null
            ? null
            : ServiceDefaults.ValidationError("date",
                $"Já existe feriado {dup.Abrangencia.ToString().ToLowerInvariant()} neste dia para este local: {dup.Name} ({dup.Date:dd/MM/yyyy}{(dup.Recorrente ? ", recorrente" : "")}).");
    }

    private static async Task<Holiday?> FindDuplicateAsync(StoreDbContext db, Holiday candidate, CancellationToken ct)
    {
        var sameLocal = await db.Holidays.AsNoTracking()
            .Where(h => h.Abrangencia == candidate.Abrangencia && h.LocalChave == candidate.LocalChave && h.Id != candidate.Id)
            .ToListAsync(ct);
        return sameLocal.FirstOrDefault(h => HolidayCalendar.SameDay(h.ToDefinition(), candidate.ToDefinition()));
    }

    private static void Apply(Holiday h, HolidayRequest req, Normalized n)
    {
        h.Abrangencia = n.Scope;
        h.Uf = n.Uf;
        h.Municipio = n.Municipio;
        h.StoreId = n.StoreId;
        h.CodigoIbge = n.Ibge;
        h.LocalChave = HolidayCalendar.LocalKey(n.Scope, n.Uf, n.Municipio, n.StoreId);
        h.Date = req.Date!.Value;
        h.Name = req.Name!.Trim();
        h.Recorrente = req.Recorrente;
        h.WorkAuthorized = req.WorkAuthorized;
        h.AuthorizationRef = req.WorkAuthorized ? req.AuthorizationRef?.Trim() : null;
        h.PontoFacultativo = req.PontoFacultativo;
        h.Funcionamento = req.Funcionamento;
        h.Abertura = req.Funcionamento == FuncionamentoDia.Especial ? n.Abertura : null;
        h.Fechamento = req.Funcionamento == FuncionamentoDia.Especial ? n.Fechamento : null;
    }

    private static HolidayChanged Event(Holiday h, string acao, string? anterior, string by) => new(
        h.Id, acao, h.Abrangencia.ToString(), h.Uf, h.Municipio, h.Date, h.Name, h.Recorrente, h.WorkAuthorized, h.AuthorizationRef,
        anterior, by, DateTime.UtcNow);

    public static void MapHolidayEndpoints(this IEndpointRouteBuilder app)
    {
        // Feriados que valem para a loja no ano (Gestor/Colaborador: só as lojas permitidas), com o funcionamento da loja
        app.MapGet("/api/stores/{id:guid}/holidays", async (Guid id, int? ano, StoreDbContext db, ICurrentUser user, CancellationToken ct) =>
        {
            if (!user.CanAccessStore(id)) return Results.Forbid();
            var store = await db.Stores.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
            if (store is null) return Results.NotFound();
            var year = ano ?? DateTime.Today.Year;
            if (year is < 1900 or > 2200) return ServiceDefaults.ValidationError("ano", "Ano inválido.");
            var all = await db.Holidays.AsNoTracking().ToListAsync(ct);
            var excecoes = await db.HolidayStoreHours.AsNoTracking().Where(x => x.StoreId == id).ToDictionaryAsync(x => x.HolidayId, ct);
            var managed = await ManagedLocalsAsync(user, db, ct);
            var byId = all.ToDictionary(h => h.Id);
            var padrao = store.PadraoFeriado();
            var list = HolidayCalendar.ForStore(all.Select(h => h.ToDefinition()), store.Location(), new DateOnly(year, 1, 1), new DateOnly(year, 12, 31))
                .Select(o =>
                {
                    var h = byId[o.Holiday.Id];
                    var excecao = excecoes.GetValueOrDefault(h.Id);
                    var efetivo = HolidayCalendar.Effective(o.Holiday, padrao, excecao?.ToHours());
                    return ToResponse(h, o.Date, CanEdit(managed, h.Abrangencia, h.LocalChave), h.StoreId == id ? store.Name : null) with
                    {
                        FuncionamentoEfetivo = efetivo.Funcionamento.ToString(),
                        AberturaEfetiva = Hm(efetivo.Abertura),
                        FechamentoEfetivo = Hm(efetivo.Fechamento),
                        FuncionamentoLoja = excecao?.Funcionamento.ToString()
                    };
                })
                .ToList();
            return Results.Ok(list);
        }).RequireAuthorization();

        var group = app.MapGroup("/api/holidays").RequireAuthorization();

        group.MapGet("/", async (HolidayScope? abrangencia, string? uf, string? municipio, int? ano, StoreDbContext db, CancellationToken ct) =>
        {
            var query = db.Holidays.AsNoTracking();
            if (abrangencia is { } a) query = query.Where(h => h.Abrangencia == a);
            if (!string.IsNullOrWhiteSpace(uf))
            {
                var u = HolidayCalendar.Normalize(uf);
                query = query.Where(h => h.Uf == u);
            }
            var list = await query.ToListAsync(ct);
            if (!string.IsNullOrWhiteSpace(municipio))
            {
                var m = HolidayCalendar.Normalize(municipio);
                list = list.Where(h => HolidayCalendar.Normalize(h.Municipio).Contains(m, StringComparison.Ordinal)).ToList();
            }
            var storeNames = await db.Stores.AsNoTracking().ToDictionaryAsync(s => s.Id, s => s.Name, ct);
            string? StoreName(Holiday h) => h.StoreId is { } sid ? storeNames.GetValueOrDefault(sid) : null;
            IEnumerable<HolidayResponse> result = ano is { } year
                ? list.SelectMany(h => HolidayCalendar.Occurrences(h.ToDefinition(), new DateOnly(year, 1, 1), new DateOnly(year, 12, 31))
                    .Select(d => ToResponse(h, d, true, StoreName(h))))
                : list.Select(h => ToResponse(h, h.Date, true, StoreName(h)));
            return Results.Ok(result.OrderBy(r => r.Date).ThenBy(r => r.Abrangencia).ThenBy(r => r.Uf).ThenBy(r => r.Municipio).ThenBy(r => r.Name));
        }).RequireAuthorization(Policies.Admin);

        // Sugestões do ano: nacionais (fixos + móveis pela Páscoa) e estaduais conhecidos da UF (padrão PE)
        group.MapGet("/sugestoes", async (int? ano, string? uf, StoreDbContext db, CancellationToken ct) =>
        {
            var year = ano ?? DateTime.Today.Year;
            if (year is < 1900 or > 2200) return ServiceDefaults.ValidationError("ano", "Ano inválido.");
            var sigla = string.IsNullOrWhiteSpace(uf) ? "PE" : uf.Trim().ToUpperInvariant();
            var existing = (await db.Holidays.AsNoTracking()
                    .Where(h => h.Abrangencia == HolidayScope.Nacional || h.Abrangencia == HolidayScope.Estadual).ToListAsync(ct))
                .Select(h => h.ToDefinition()).ToList();
            var list = HolidayCalendar.NationalSuggestions(year).Concat(HolidayCalendar.StateSuggestions(sigla, year))
                .Select(s =>
                {
                    var key = HolidayCalendar.LocalKey(s.Abrangencia, s.Uf, null);
                    var cadastrado = existing.Any(h => h.Abrangencia == s.Abrangencia
                                                       && HolidayCalendar.LocalKey(h.Abrangencia, h.Uf, h.Municipio) == key
                                                       && HolidayCalendar.Occurrences(h, s.Date, s.Date).Any());
                    return new HolidaySuggestionResponse(s.Date, s.Name, s.Abrangencia, s.Uf, s.Recorrente, s.PontoFacultativo, s.Observacao, cadastrado);
                })
                .OrderBy(s => s.Date).ToList();
            return Results.Ok(list);
        }).RequireAuthorization(Policies.Admin);

        // Importa as sugestões confirmadas (pula as já cadastradas no dia/local)
        group.MapPost("/importar", async (HolidayImportRequest req, StoreDbContext db, IPublishEndpoint bus, ICurrentUser user, CancellationToken ct) =>
        {
            var itens = req.Itens ?? [];
            if (itens.Count == 0) return ServiceDefaults.ValidationError("itens", "Escolha ao menos um feriado para importar.");
            if (itens.Count > 100) return ServiceDefaults.ValidationError("itens", "No máximo 100 feriados por importação.");
            var criados = new List<Holiday>();
            var ignorados = new List<string>();
            foreach (var item in itens)
            {
                if (Validate(item, out var n) is not null)
                {
                    ignorados.Add($"{item.Name} ({item.Date:dd/MM/yyyy}): dados inválidos");
                    continue;
                }
                var h = new Holiday { CriadoPor = user.Display };
                Apply(h, item, n);
                if (await FindDuplicateAsync(db, h, ct) is not null || criados.Any(c => c.LocalChave == h.LocalChave && c.Abrangencia == h.Abrangencia
                        && HolidayCalendar.SameDay(c.ToDefinition(), h.ToDefinition())))
                {
                    ignorados.Add($"{h.Name} ({h.Date:dd/MM/yyyy}): já cadastrado");
                    continue;
                }
                db.Holidays.Add(h);
                criados.Add(h);
            }
            await db.SaveChangesAsync(ct);
            foreach (var h in criados) await bus.Publish(Event(h, "criado", null, user.Display), ct);
            return Results.Ok(new { criados = criados.Count, ignorados });
        }).RequireAuthorization(Policies.Admin);

        group.MapGet("/{id:guid}", async (Guid id, StoreDbContext db, ICurrentUser user, CancellationToken ct) =>
        {
            var h = await db.Holidays.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (h is null) return Results.NotFound();
            var managed = await ManagedLocalsAsync(user, db, ct);
            return Results.Ok(ToResponse(h, h.Date, CanEdit(managed, h.Abrangencia, h.LocalChave)));
        });

        group.MapPost("/", async (HolidayRequest req, StoreDbContext db, IPublishEndpoint bus, ICurrentUser user, CancellationToken ct) =>
        {
            if (Validate(req, out var n) is { } invalid) return invalid;
            if (n.StoreId is { } sid && !await db.Stores.AnyAsync(s => s.Id == sid, ct)) return ServiceDefaults.ValidationError("storeId", "Loja não encontrada.");
            var managed = await ManagedLocalsAsync(user, db, ct);
            var h = new Holiday { CriadoPor = user.Display };
            Apply(h, req, n);
            if (!CanEdit(managed, h.Abrangencia, h.LocalChave)) return ForbiddenScope(user, n.Scope);
            if (await DuplicateAsync(db, h, ct) is { } dup) return dup;
            db.Holidays.Add(h);
            await db.SaveChangesAsync(ct);
            await bus.Publish(Event(h, "criado", null, user.Display), ct);
            return Results.Created($"/api/holidays/{h.Id}", ToResponse(h, h.Date, true));
        }).RequireAuthorization(Policies.Manager);

        group.MapPut("/{id:guid}", async (Guid id, HolidayRequest req, StoreDbContext db, IPublishEndpoint bus, ICurrentUser user, CancellationToken ct) =>
        {
            var h = await db.Holidays.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (h is null) return Results.NotFound();
            if (Validate(req, out var n) is { } invalid) return invalid;
            if (n.StoreId is { } sid && !await db.Stores.AnyAsync(s => s.Id == sid, ct)) return ServiceDefaults.ValidationError("storeId", "Loja não encontrada.");
            var managed = await ManagedLocalsAsync(user, db, ct);
            // O gestor precisa poder mexer no feriado como está E no resultado (não move feriado para outro município/loja)
            if (!CanEdit(managed, h.Abrangencia, h.LocalChave)) return ForbiddenScope(user, h.Abrangencia);
            var anterior = Resumo(h);
            Apply(h, req, n);
            if (!CanEdit(managed, h.Abrangencia, h.LocalChave)) return ForbiddenScope(user, n.Scope);
            if (await DuplicateAsync(db, h, ct) is { } dup) return dup;
            h.AtualizadoPor = user.Display;
            h.AtualizadoEm = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            await bus.Publish(Event(h, "alterado", anterior, user.Display), ct);
            return Results.Ok(ToResponse(h, h.Date, true));
        }).RequireAuthorization(Policies.Manager);

        group.MapDelete("/{id:guid}", async (Guid id, StoreDbContext db, IPublishEndpoint bus, ICurrentUser user, CancellationToken ct) =>
        {
            var h = await db.Holidays.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (h is null) return Results.NotFound();
            var managed = await ManagedLocalsAsync(user, db, ct);
            if (!CanEdit(managed, h.Abrangencia, h.LocalChave)) return ForbiddenScope(user, h.Abrangencia);
            db.Holidays.Remove(h);
            await db.SaveChangesAsync(ct);
            await bus.Publish(Event(h, "excluido", null, user.Display), ct);
            return Results.NoContent();
        }).RequireAuthorization(Policies.Manager);
    }
}

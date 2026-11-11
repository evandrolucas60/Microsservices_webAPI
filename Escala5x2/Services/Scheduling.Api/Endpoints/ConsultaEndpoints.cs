using Escala5x2.BuildingBlocks.Contracts.Http;
using Escala5x2.BuildingBlocks.Contracts.Planning;
using Escala5x2.BuildingBlocks.Shared.Auth;
using Escala5x2.BuildingBlocks.Shared.Web;
using Microsoft.EntityFrameworkCore;
using Scheduling.Api.Clients;
using Scheduling.Api.Data;
using Scheduling.Api.Domain;
using Scheduling.Api.Services;

namespace Scheduling.Api.Endpoints;

/// <summary>
/// GET /api/schedules/consulta: escalas que valem oficialmente (só Publicadas) da loja no período, para a página de
/// consulta da intranet (somente leitura). Sem custo/hora, observação, título, diagnóstico, conformidade, auditoria nem
/// quem gerou/aprovou: só o necessário para ver quem trabalha em cada dia.
/// <paramref name="AusenciasAtuais"/>: false = Employee.Api fora do ar, afastamentos da fotografia das escalas.
/// </summary>
public sealed record ConsultaResponse(
    Guid StoreId, string? StoreName, DateOnly Inicio, DateOnly Fim, IReadOnlyList<ConsultaSetor> Setores, bool AusenciasAtuais);

/// <summary>Setor da loja e as escalas publicadas exibidas no período (vazio = sem escala publicada).</summary>
public sealed record ConsultaSetor(Guid SectorId, string Nome, IReadOnlyList<ConsultaEscala> Escalas);

/// <summary>
/// Escala publicada do setor que vale em ao menos um dia do período. Por setor e dia vale a PRINCIPAL; sem ela, a
/// publicada mais recente. <paramref name="Dias"/>: os dias do período em que esta escala é a exibida.
/// </summary>
public sealed record ConsultaEscala(
    Guid Id, int Version, DateOnly StartDate, DateOnly EndDate, bool Principal, DateTime? PublishedAt,
    IReadOnlyList<DateOnly> Dias, IReadOnlyList<ConsultaColaborador> Colaboradores, IReadOnlyList<ConsultaCelula> Celulas,
    IReadOnlyList<ConsultaFeriado> Feriados);

/// <param name="Cargo">Cargo do cadastro atual (null = não informado ou Employee.Api fora do ar).</param>
/// <param name="PerfilJornada">Abertura | Fechamento | Flexivel (null = escala sem operação).</param>
public sealed record ConsultaColaborador(Guid Id, string Nome, string? Cargo, string? PerfilJornada);

/// <summary>
/// Célula mínima: <paramref name="Tipo"/> T (trabalho), F (folga) ou AF (afastado: sem turno nem horário). O tipo do
/// afastamento (<paramref name="Afastamento"/>) só vai para Admin, RH, Jurídico e Gestor; os demais recebem "Afastado"
/// (LGPD). Dia sem célula: folga nas escalas do motor sem folga explícita; vazio nas que marcam a folga.
/// </summary>
public sealed record ConsultaCelula(
    Guid EmployeeId, DateOnly Data, string Tipo, string? Turno, string? TipoTurno, TimeOnly? Inicio, TimeOnly? Fim,
    int? MinutosPagos, TimeOnly? InicioAlmoco, int? MinutosAlmoco, string? Afastamento);

/// <summary>Feriado do período da escala (sem autorização = ninguém trabalha e não é folga).</summary>
public sealed record ConsultaFeriado(DateOnly Data, string Nome, bool TrabalhoAutorizado);

public static class ConsultaEndpoints
{
    internal const string Trabalho = "T";
    internal const string Folga = "F";
    internal const string Afastado = "AF";

    /// <summary>
    /// Por dia, a escala exibida entre as publicadas do setor: a principal que cobre o dia; sem ela, a publicada mais
    /// recente (publicada por último; empate: a pedida mais recente). A página ordena igual (publishedAt).
    /// </summary>
    internal static Dictionary<DateOnly, Guid> EscolherPorDia<T>(IEnumerable<T> escalas, Func<T, Guid> id, Func<T, DateOnly> inicio,
        Func<T, DateOnly> fim, Func<T, bool> principal, Func<T, DateTime?> publicadaEm, Func<T, DateTime> pedidaEm,
        DateOnly de, DateOnly ate)
    {
        var ordenadas = escalas.OrderByDescending(principal).ThenByDescending(publicadaEm).ThenByDescending(pedidaEm).ToList();
        var porDia = new Dictionary<DateOnly, Guid>();
        for (var d = de; d <= ate; d = d.AddDays(1))
        {
            var dia = d;
            var escolhida = ordenadas.FirstOrDefault(s => inicio(s) <= dia && dia <= fim(s));
            if (escolhida is not null) porDia[dia] = id(escolhida);
        }
        return porDia;
    }

    public static void MapConsultaEndpoints(this IEndpointRouteBuilder app)
    {
        // Qualquer usuário logado; Gestor e Colaborador só as lojas permitidas: perfil ∪ roles de loja (Admin, RH e
        // Jurídico, qualquer loja)
        app.MapGet("/api/schedules/consulta", async (Guid storeId, DateOnly inicio, DateOnly fim, Guid? sectorId,
            SchedulingDbContext db, ICurrentUser user, PlanningClients clients, ILogger<AbsenceSync> logger, CancellationToken ct) =>
        {
            if (fim < inicio) return ServiceDefaults.ValidationError("fim", "A data final é anterior à inicial.");
            if (fim.DayNumber - inicio.DayNumber > 62) return ServiceDefaults.ValidationError("fim", "Informe um período de no máximo 62 dias.");
            if (!user.CanAccessStore(storeId)) return Results.Forbid();

            // Só Publicadas por setor (escalas antigas por loja ficam de fora) cujo período (do mês ou, nas antigas, por
            // semanas) cruza [inicio, fim]. Não carrega conformidade/diagnóstico/soluções.
            var query = db.Schedules.AsNoTracking()
                .Where(s => s.StoreId == storeId && s.SectorId != null && s.Status == ScheduleStatus.Published
                            && s.StartDate <= fim && s.EndDate >= inicio);
            if (sectorId is { } sec) query = query.Where(s => s.SectorId == sec);
            var publicadas = await query
                .Select(s => new
                {
                    s.Id, SectorId = s.SectorId!.Value, s.SectorName, s.StoreName, s.StartDate, s.EndDate, s.Version, s.Principal,
                    s.PublishedAt, s.RequestedAt, s.SnapshotJson
                })
                .ToListAsync(ct);

            // Escala exibida em cada setor e dia; as que não valem em nenhum dia do período não vão
            var escolha = publicadas.GroupBy(s => s.SectorId).ToDictionary(g => g.Key, g => EscolherPorDia(g, s => s.Id, s => s.StartDate,
                s => s.EndDate, s => s.Principal, s => s.PublishedAt, s => s.RequestedAt, inicio, fim));
            var usadas = publicadas.Where(s => escolha[s.SectorId].ContainsValue(s.Id)).ToList();
            var ids = usadas.Select(s => s.Id).ToList();
            var celulas = (await db.Assignments.AsNoTracking()
                    .Where(a => ids.Contains(a.ScheduleId) && a.Date >= inicio && a.Date <= fim)
                    .Select(a => new { a.ScheduleId, a.EmployeeId, a.Date, a.Folga, a.ShiftId, a.ShiftCode, a.Start, a.End, a.PaidMinutes, a.BreakStart, a.BreakMinutes })
                    .ToListAsync(ct))
                .ToLookup(a => a.ScheduleId);

            // Afastamentos ATUAIS (um pedido para o período todo); Employee.Api fora do ar: os da fotografia de cada escala
            List<AbsenceEntry>? atuais = null;
            if (usadas.Count > 0)
            {
                try
                {
                    atuais = (await clients.GetAbsencesAsync(storeId, inicio, fim, ct)).ToList();
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    logger.LogWarning("Consulta: ausências atuais indisponíveis no Employee.Api para a loja {StoreId} ({Motivo}); usando as das fotografias",
                        storeId, $"{ex.GetType().Name}: {ex.Message}");
                }
            }

            // Cargo do cadastro atual (best effort) e setores da loja (para listar também os setores sem escala publicada)
            var cargos = new Dictionary<Guid, string>();
            IReadOnlyList<SectorSnapshot>? setoresLoja = null;
            string? nomeLoja = publicadas.FirstOrDefault()?.StoreName;
            if (usadas.Count > 0)
            {
                try
                {
                    foreach (var e in await clients.GetEmployeesAsync(storeId, inicio, fim, ct))
                        if (!string.IsNullOrWhiteSpace(e.JobTitle)) cargos[e.EmployeeId] = e.JobTitle;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    logger.LogWarning("Consulta: cargos indisponíveis no Employee.Api para a loja {StoreId} ({Motivo})",
                        storeId, $"{ex.GetType().Name}: {ex.Message}");
                }
            }
            try
            {
                var loja = await clients.GetStorePlanningAsync(storeId, inicio, fim, ct);
                setoresLoja = loja?.Sectors;
                nomeLoja ??= loja?.StoreName;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning("Consulta: setores da loja {StoreId} indisponíveis no Store.Api ({Motivo}); só os setores com escala publicada",
                    storeId, $"{ex.GetType().Name}: {ex.Message}");
            }

            var tipoVisivel = ScheduleEndpoints.SeesAbsenceType(user, storeId);
            string Rotulo(string tipo) => tipoVisivel ? UnavailabilityKinds.Rotulo(tipo) : UnavailabilityKinds.RotuloGenerico;

            var escalasPorSetor = new Dictionary<Guid, List<ConsultaEscala>>();
            foreach (var s in usadas.OrderBy(s => s.StartDate))
            {
                var fimEscala = s.EndDate;
                var de = s.StartDate > inicio ? s.StartDate : inicio;
                var ate = fimEscala < fim ? fimEscala : fim;
                var snapshot = ScheduleCells.ReadSnapshot(new Schedule { SnapshotJson = s.SnapshotJson });
                var empregados = snapshot.Employees ?? [];
                var turnos = (snapshot.Shifts ?? []).GroupBy(x => x.ShiftId).ToDictionary(g => g.Key, g => g.First());
                var pessoas = empregados.Select(e => e.EmployeeId).ToHashSet();

                var ausencias = (atuais is not null
                        ? atuais.Where(a => pessoas.Contains(a.EmployeeId) && a.Date >= de && a.Date <= ate)
                        : empregados.SelectMany(e => AbsenceSync.Absences(e, de, ate).Select(a => new AbsenceEntry(e.EmployeeId, a.Date, a.Tipo))))
                    .GroupBy(a => (a.EmployeeId, a.Date))
                    .ToDictionary(g => g.Key, g => g.OrderBy(a => UnavailabilityKinds.Rank(a.Tipo)).First().Tipo);

                var lista = new List<ConsultaCelula>();
                var comCelula = new HashSet<(Guid, DateOnly)>();
                foreach (var a in celulas[s.Id].Where(a => a.Date >= de && a.Date <= ate))
                {
                    comCelula.Add((a.EmployeeId, a.Date));
                    // Dia de afastamento: "AF" sem turno nem horário (nem folga)
                    if (ausencias.TryGetValue((a.EmployeeId, a.Date), out var tipo))
                    {
                        lista.Add(new ConsultaCelula(a.EmployeeId, a.Date, Afastado, null, null, null, null, null, null, null, Rotulo(tipo)));
                        continue;
                    }
                    if (a.Folga || a.ShiftId is null)
                    {
                        lista.Add(new ConsultaCelula(a.EmployeeId, a.Date, Folga, null, null, null, null, null, null, null, null));
                        continue;
                    }
                    var turno = turnos.GetValueOrDefault(a.ShiftId.Value);
                    lista.Add(new ConsultaCelula(a.EmployeeId, a.Date, Trabalho, a.ShiftCode, turno?.Tipo, a.Start, a.End, a.PaidMinutes,
                        a.BreakStart, a.BreakStart is null ? null : a.BreakMinutes ?? turno?.BreakMinutes, null));
                }
                // Afastado em dia sem célula
                foreach (var ((emp, dia), tipo) in ausencias)
                    if (!comCelula.Contains((emp, dia)))
                        lista.Add(new ConsultaCelula(emp, dia, Afastado, null, null, null, null, null, null, null, Rotulo(tipo)));

                var operacao = snapshot.Operacao is not null;
                var escala = new ConsultaEscala(s.Id, s.Version, s.StartDate, fimEscala, s.Principal, s.PublishedAt,
                    escolha[s.SectorId].Where(kv => kv.Value == s.Id).Select(kv => kv.Key).Order().ToList(),
                    empregados.OrderBy(e => e.Name)
                        .Select(e => new ConsultaColaborador(e.EmployeeId, e.Name, cargos.GetValueOrDefault(e.EmployeeId),
                            operacao ? e.Jornada?.PerfilJornada : null))
                        .ToList(),
                    lista.OrderBy(c => c.Data).ToList(),
                    (snapshot.Holidays ?? []).Where(h => h.Date >= de && h.Date <= ate).OrderBy(h => h.Date)
                        .Select(h => new ConsultaFeriado(h.Date, h.Name, h.WorkAuthorized)).ToList());
                if (!escalasPorSetor.TryGetValue(s.SectorId, out var doSetor)) escalasPorSetor[s.SectorId] = doSetor = [];
                doSetor.Add(escala);
            }

            // Setores: os da loja (com ou sem escala publicada); sem o cadastro, os das escalas encontradas
            var nomes = new Dictionary<Guid, string>();
            foreach (var x in setoresLoja ?? []) nomes.TryAdd(x.SectorId, x.Name);
            foreach (var s in publicadas) nomes.TryAdd(s.SectorId, s.SectorName ?? Schedule.AllSectors);
            var setores = nomes
                .Where(kv => sectorId is not { } filtro || kv.Key == filtro)
                .Select(kv => new ConsultaSetor(kv.Key, kv.Value, escalasPorSetor.GetValueOrDefault(kv.Key) ?? []))
                .OrderBy(x => x.Nome, StringComparer.Create(new System.Globalization.CultureInfo("pt-BR"), true))
                .ToList();

            return Results.Ok(new ConsultaResponse(storeId, nomeLoja, inicio, fim, setores, atuais is not null || usadas.Count == 0));
        }).RequireAuthorization();
    }
}

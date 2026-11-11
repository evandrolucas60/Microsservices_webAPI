using Escala5x2.BuildingBlocks.Contracts.Planning;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Store.Api.Data;
using Store.Api.Domain;
using Store.Api.Endpoints;
using Store.Api.Lg;
using Xunit;

namespace Store.Tests;

/// <summary>
/// Cadastro da loja (localização e horário de funcionamento), abrangência dos feriados (Nacional, Estadual, Municipal por
/// nome ou IBGE, Loja), datas móveis pela Páscoa, funcionamento no feriado, picos manuais na fotografia e a sincronização
/// com a LG preservando o cadastro manual.
/// </summary>
public class StoreCadastroTests
{
    private static readonly Guid Cabo = Guid.NewGuid();
    private static readonly StoreLocation LojaCabo = new(Cabo, "Cabo de Santo Agostinho", "PE", "2603009");

    private static HolidayDefinition H(HolidayScope scope, string? uf = null, string? municipio = null, Guid? storeId = null, string? ibge = null,
        bool autorizado = true, bool facultativo = false, FuncionamentoDia? funcionamento = null, TimeOnly? abre = null, TimeOnly? fecha = null,
        DateOnly? date = null) =>
        new(Guid.NewGuid(), scope, uf, municipio, date ?? new DateOnly(2026, 12, 8), "Feriado", false, autorizado, autorizado ? "CCT" : null,
            facultativo, storeId, ibge, funcionamento, abre, fecha);

    // ---------------- Abrangência ----------------

    [Fact]
    public void Scope_resolution_by_location_ibge_and_store()
    {
        Assert.True(HolidayCalendar.AppliesTo(H(HolidayScope.Nacional), LojaCabo));
        Assert.True(HolidayCalendar.AppliesTo(H(HolidayScope.Estadual, "pe"), LojaCabo));
        Assert.False(HolidayCalendar.AppliesTo(H(HolidayScope.Estadual, "PB"), LojaCabo));
        // Municipal pelo nome (sem acento/maiúsculas) e pelo IBGE
        Assert.True(HolidayCalendar.AppliesTo(H(HolidayScope.Municipal, "PE", "CABO DE SANTO  AGOSTINHO"), LojaCabo));
        Assert.True(HolidayCalendar.AppliesTo(H(HolidayScope.Municipal, "PE", "Cabo", ibge: "2603009"), LojaCabo)); // IBGE prevalece
        Assert.False(HolidayCalendar.AppliesTo(H(HolidayScope.Municipal, "PE", "Cabo de Santo Agostinho", ibge: "2611606"), LojaCabo));
        Assert.False(HolidayCalendar.AppliesTo(H(HolidayScope.Municipal, "PE", "Recife"), LojaCabo));
        // Loja: só a própria
        Assert.True(HolidayCalendar.AppliesTo(H(HolidayScope.Loja, storeId: Cabo), LojaCabo));
        Assert.False(HolidayCalendar.AppliesTo(H(HolidayScope.Loja, storeId: Guid.NewGuid()), LojaCabo));
        // Loja sem localização: só nacionais e os da loja
        var semLocal = new StoreLocation(Cabo, "", "");
        Assert.False(HolidayCalendar.AppliesTo(H(HolidayScope.Estadual, "PE"), semLocal));
        Assert.False(HolidayCalendar.AppliesTo(H(HolidayScope.Municipal, "PE", "Cabo de Santo Agostinho"), semLocal));
        Assert.True(HolidayCalendar.AppliesTo(H(HolidayScope.Loja, storeId: Cabo), semLocal));
    }

    [Fact]
    public void Local_key_of_store_scope_is_per_store()
    {
        Assert.Equal($"LOJA|{Cabo:N}", HolidayCalendar.LocalKey(HolidayScope.Loja, null, null, Cabo));
        Assert.Equal("PE|CABO DE SANTO AGOSTINHO", HolidayCalendar.LocalKey(HolidayScope.Municipal, "pe", "Cabo de Santo Agostinho"));
    }

    // ---------------- Páscoa e sugestões ----------------

    [Theory]
    [InlineData(2024, 3, 31)]
    [InlineData(2025, 4, 20)]
    [InlineData(2026, 4, 5)]
    [InlineData(2027, 3, 28)]
    [InlineData(2038, 4, 25)]
    public void Easter_dates(int year, int month, int day) => Assert.Equal(new DateOnly(year, month, day), HolidayCalendar.Easter(year));

    [Fact]
    public void National_suggestions_include_movable_holidays_and_facultative_flags()
    {
        var s = HolidayCalendar.NationalSuggestions(2026).ToDictionary(x => x.Name);
        Assert.Equal(new DateOnly(2026, 2, 16), s["Carnaval (segunda-feira)"].Date);
        Assert.Equal(new DateOnly(2026, 2, 17), s["Carnaval (terça-feira)"].Date);
        Assert.True(s["Carnaval (terça-feira)"].PontoFacultativo);
        Assert.False(s["Carnaval (terça-feira)"].Recorrente);
        Assert.Equal(new DateOnly(2026, 4, 3), s["Sexta-feira Santa"].Date);
        Assert.False(s["Sexta-feira Santa"].PontoFacultativo);
        Assert.Equal(new DateOnly(2026, 6, 4), s["Corpus Christi"].Date);
        Assert.True(s["Corpus Christi"].PontoFacultativo);
        Assert.True(s["Natal"].Recorrente);
        Assert.Equal(new DateOnly(2026, 11, 20), s["Dia Nacional de Zumbi e da Consciência Negra"].Date);
        Assert.Equal(13, s.Count);

        var pe = HolidayCalendar.StateSuggestions("pe", 2026);
        Assert.Contains(pe, x => x.Date == new DateOnly(2026, 3, 6) && x.Abrangencia == HolidayScope.Estadual && x.Uf == "PE");
        Assert.Contains(pe, x => x.Date == new DateOnly(2026, 6, 24));
        Assert.Empty(HolidayCalendar.StateSuggestions("SP", 2026));
    }

    // ---------------- Funcionamento no feriado ----------------

    [Fact]
    public void Effective_hours_follow_override_holiday_store_default_and_legal_closure()
    {
        var padrao = new HolidayHours(FuncionamentoDia.Especial, new TimeOnly(8, 0), new TimeOnly(14, 0));
        // Sem autorização (feriado legal): sempre fechada, mesmo com exceção
        Assert.Equal(FuncionamentoDia.Fechada, HolidayCalendar.Effective(H(HolidayScope.Nacional, autorizado: false), padrao,
            new HolidayHours(FuncionamentoDia.Normal)).Funcionamento);
        // Ponto facultativo sem convenção: segue o padrão da loja
        Assert.Equal(padrao, HolidayCalendar.Effective(H(HolidayScope.Nacional, autorizado: false, facultativo: true), padrao));
        // Feriado define o funcionamento; a exceção da loja prevalece
        var feriadoFechado = H(HolidayScope.Nacional, funcionamento: FuncionamentoDia.Fechada);
        Assert.Equal(FuncionamentoDia.Fechada, HolidayCalendar.Effective(feriadoFechado, padrao).Funcionamento);
        Assert.Equal(FuncionamentoDia.Normal, HolidayCalendar.Effective(feriadoFechado, padrao, new HolidayHours(FuncionamentoDia.Normal)).Funcionamento);
        // Sem nada: Normal
        Assert.Equal(FuncionamentoDia.Normal, HolidayCalendar.Effective(H(HolidayScope.Nacional)).Funcionamento);
    }

    [Fact]
    public void Snapshot_merges_the_day_with_the_most_restrictive_hours()
    {
        var day = new DateOnly(2026, 12, 24);
        var a = H(HolidayScope.Nacional, funcionamento: FuncionamentoDia.Especial, abre: new TimeOnly(7, 0), fecha: new TimeOnly(18, 0), date: day);
        var b = H(HolidayScope.Loja, storeId: Cabo, funcionamento: FuncionamentoDia.Especial, abre: new TimeOnly(8, 0), fecha: new TimeOnly(16, 0), date: day);
        var snap = Assert.Single(HolidayCalendar.Snapshot([a, b], LojaCabo, day, day));
        Assert.Equal(nameof(FuncionamentoDia.Especial), snap.Funcionamento);
        Assert.Equal((new TimeOnly(8, 0), new TimeOnly(16, 0)), (snap.Abertura, snap.Fechamento));
        Assert.True(snap.WorkAuthorized);

        var fechado = H(HolidayScope.Estadual, "PE", autorizado: false, date: day);
        var closed = Assert.Single(HolidayCalendar.Snapshot([a, fechado], LojaCabo, day, day));
        Assert.Equal(nameof(FuncionamentoDia.Fechada), closed.Funcionamento);
        Assert.False(closed.WorkAuthorized);

        // Exceção da loja para o feriado a
        var excecao = new Dictionary<Guid, HolidayHours> { [a.Id] = new(FuncionamentoDia.Fechada) };
        Assert.Equal(nameof(FuncionamentoDia.Fechada), Assert.Single(HolidayCalendar.Snapshot([a], LojaCabo, day, day, null, excecao)).Funcionamento);
    }

    // ---------------- Picos manuais e horário ----------------

    [Fact]
    public void Store_wide_manual_peak_becomes_one_row_per_sector()
    {
        var s1 = new Sector { Name = "Caixa" };
        var s2 = new Sector { Name = "Padaria" };
        var peak = new ManualPeak { DiaSemana = DayOfWeek.Saturday, Inicio = new TimeOnly(11, 0), Fim = new TimeOnly(14, 0), Quantidade = 4 };
        var datePeak = new ManualPeak { SectorId = s2.Id, Data = new DateOnly(2026, 12, 24), Inicio = new TimeOnly(9, 0), Fim = new TimeOnly(12, 0), Adicional = 2 };
        var rows = StoreCadastroEndpoints.PeakRows([peak, datePeak], [s1, s2]).ToList();

        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.True(r.PicoManual));
        Assert.Equal([s1.Id, s2.Id], rows.Where(r => r.Data is null).Select(r => r.SectorId));
        Assert.Equal("Pico manual 11:00–14:00 de sábado", rows[0].Rotulo);
        var d = Assert.Single(rows, r => r.Data is not null);
        Assert.Equal((DayOfWeek.Thursday, 0, 2), (d.DayOfWeek, d.MinStaff, d.Adicional!.Value));
    }

    [Fact]
    public void Standard_window_is_the_most_frequent_open_hours()
    {
        var hours = Enum.GetValues<DayOfWeek>().Select(d => d switch
        {
            DayOfWeek.Sunday => new StoreHours { DiaSemana = d, Fechada = true },
            DayOfWeek.Saturday => new StoreHours { DiaSemana = d, Abertura = new TimeOnly(7, 0), Fechamento = new TimeOnly(18, 0) },
            _ => new StoreHours { DiaSemana = d, Abertura = new TimeOnly(7, 0), Fechamento = new TimeOnly(22, 0) }
        }).ToList();
        Assert.Equal((new TimeOnly(7, 0), new TimeOnly(22, 0)), StoreCadastroEndpoints.StandardWindow(hours));
        Assert.Null(StoreCadastroEndpoints.StandardWindow([]));
        var dayHours = StoreCadastroEndpoints.DayHoursOf(hours)!;
        Assert.Null(OperationPlan.DayWindow(new OperationSnapshot(new TimeOnly(7, 0), new TimeOnly(22, 0), 2, 2, 2, 15, null, null, 500, 60,
            Horarios: dayHours), DayOfWeek.Sunday));
    }

    // ---------------- Turnos automáticos ----------------

    [Fact]
    public void Operations_use_automatic_shifts_by_default_and_keep_the_personalized_journey()
    {
        // Migration TurnosAutomaticos: coluna com padrão true (todas as operações existentes passam a usar); a jornada
        // personalizada fica guardada e só vale com "Personalizar turnos"
        var op = new StoreOperation { Abertura = new TimeOnly(7, 0), Fechamento = new TimeOnly(22, 0), CoberturaMinima = 1, MinimoAbertura = 1,
            MinimoFechamento = 1, IncrementoFlexivelMinutos = 30, DuracaoJornadaMinutos = 588, IntervaloMinutos = 60 };
        Assert.True(op.TurnosAutomaticos);
        var snap = op.ToSnapshot();
        Assert.True(snap.TurnosAutomaticos);
        Assert.Equal(588, snap.DuracaoJornadaMinutos);
        // Com turnos automáticos o catálogo fica só manual e a jornada não é validada
        Assert.Null(OperationPlan.Classify(snap, new TimeOnly(7, 0), new TimeOnly(16, 48)));
        Assert.Empty(OperationPlan.Validate(snap with { DuracaoJornadaMinutos = 0 }));
        Assert.Equal(JourneyProfiles.Abertura, OperationPlan.Classify(snap with { TurnosAutomaticos = false }, new TimeOnly(7, 0), new TimeOnly(16, 48)));
    }

    // ---------------- LG não sobrescreve o cadastro ----------------

    [Fact]
    public void Lg_sync_keeps_manual_location_and_hours()
    {
        var loja = new Api.Domain.Store
        {
            Code = "01", Name = "VAREJO LOJA 01 - CABO", LgCentroDeCustoCodigo = "201000", City = "Cabo de Santo Agostinho", State = "PE",
            CodigoIbge = "2603009", Logradouro = "Rodovia BR-101", Numero = "1000", Bairro = "Centro", Cep = "54500000",
            FeriadoFuncionamento = FuncionamentoDia.Especial, FeriadoAbertura = new TimeOnly(8, 0), FeriadoFechamento = new TimeOnly(14, 0)
        };
        loja.Horarios.Add(new StoreHours { StoreId = loja.Id, DiaSemana = DayOfWeek.Sunday, Fechada = true });
        var centros = LgCentrosDeCustoClient.LerResposta(LgCentrosFixtures.Resposta(1, 1, 1, LgCentrosFixtures.Centro("201000", "VAREJO LOJA 01 - CABO NOVO"))).Itens;

        LgLojasRegras.Aplicar(centros, [loja], new Escala5x2.BuildingBlocks.Shared.Lg.LgLojasOptions(), new DateOnly(2026, 11, 4), "Teste",
            new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc));

        Assert.Equal("VAREJO LOJA 01 - CABO NOVO", loja.Name); // nome segue a LG
        Assert.Equal(("Cabo de Santo Agostinho", "PE", "2603009"), (loja.City, loja.State, loja.CodigoIbge));
        Assert.Equal(("Rodovia BR-101", "1000", "Centro", "54500000"), (loja.Logradouro, loja.Numero, loja.Bairro, loja.Cep));
        Assert.Equal(FuncionamentoDia.Especial, loja.FeriadoFuncionamento);
        Assert.Single(loja.Horarios);
    }

    // ---------------- Migrations ----------------

    [Fact]
    public void Model_matches_the_migrations_snapshot()
    {
        var options = new DbContextOptionsBuilder<StoreDbContext>().UseNpgsql("Host=localhost;Database=nao_conecta").Options;
        using var db = new StoreDbContext(options);
        var snapshot = db.GetService<IMigrationsAssembly>().ModelSnapshot!.Model;
        if (snapshot is IMutableModel mutable) snapshot = mutable.FinalizeModel();
        snapshot = db.GetService<IModelRuntimeInitializer>().Initialize(snapshot);
        var diffs = db.GetService<IMigrationsModelDiffer>()
            .GetDifferences(snapshot.GetRelationalModel(), db.GetService<IDesignTimeModel>().Model.GetRelationalModel());
        Assert.Empty(diffs);
    }
}

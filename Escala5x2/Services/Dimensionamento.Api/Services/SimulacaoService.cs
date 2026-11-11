using System.Globalization;
using System.Text.Json;
using Dimensionamento.Api.Calculo;
using Dimensionamento.Api.Clients;
using Dimensionamento.Api.Data;
using Dimensionamento.Api.Domain;
using Dimensionamento.Api.Endpoints;
using Escala5x2.BuildingBlocks.Compliance;
using Escala5x2.BuildingBlocks.Contracts.Compliance;
using Escala5x2.BuildingBlocks.Contracts.Planning;
using Microsoft.EntityFrameworkCore;

namespace Dimensionamento.Api.Services;

/// <summary>Erro de validação do cenário (vira 400 com o campo).</summary>
public sealed class ValidacaoException(string campo, string mensagem) : Exception(mensagem)
{
    public string Campo { get; } = campo;
}

/// <summary>
/// Monta o cenário (cenário → operação da loja/setor → regras vigentes → padrões), consulta os outros serviços
/// (degradando com aviso quando algum está fora do ar) e roda o <see cref="Dimensionador"/>.
/// </summary>
public sealed class SimulacaoService(DimensionamentoDbContext db, EscalaClients clients, IConfiguration config, ILogger<SimulacaoService> logger)
{
    public const string Padrao = "padrão";

    public static DateOnly ProximaSegunda(DateOnly hoje) =>
        hoje.DayOfWeek == DayOfWeek.Monday ? hoje : hoje.AddDays(((int)DayOfWeek.Monday - (int)hoje.DayOfWeek + 7) % 7);

    public async Task<SimulacaoResponse> SimularAsync(SimulacaoRequest req, CancellationToken ct)
    {
        var (perfil, curva, fatores) = await CurvaAsync(req, ct);
        var ctx = await ResolverAsync(req, ct);
        var demanda = CurvaDemanda.Matriz(curva, fatores);
        var entrada = ctx.Entrada with { Demanda = demanda };

        ResultadoCalculo resultado;
        try
        {
            // CPU (CP-SAT): fora da thread da requisição
            resultado = await Task.Run(() => Dimensionador.Calcular(entrada), ct);
        }
        catch (ArgumentException ex)
        {
            throw new ValidacaoException("parametros", ex.Message);
        }

        if (resultado.Escala is { } esc && ctx.Escala is { } info)
        {
            resultado = resultado with
            {
                Escala = esc with
                {
                    EscalaId = info.Id,
                    Titulo = info.Titulo ?? info.SectorName,
                    Periodo = $"{info.StartDate:dd/MM/yyyy} a {info.EndDate:dd/MM/yyyy}",
                    DiasConsiderados = info.EndDate.DayNumber - info.StartDate.DayNumber + 1,
                }
            };
        }
        var avisos = ctx.Avisos.Concat(resultado.Avisos).Distinct().ToList();
        return new SimulacaoResponse(ctx.Parametros, ctx.Origens, perfil, ctx.Loja, resultado with { Avisos = avisos }, avisos, ctx.Servicos, DateTime.UtcNow);
    }

    /// <summary>Curva do perfil salvo ou a digitada no cenário.</summary>
    private async Task<(PerfilResumo? Perfil, List<PontoCurva> Curva, List<double>? Fatores)> CurvaAsync(SimulacaoRequest req, CancellationToken ct)
    {
        if (req.PerfilDemandaId is { } pid)
        {
            var p = await db.Perfis.AsNoTracking().FirstOrDefaultAsync(x => x.Id == pid, ct)
                ?? throw new ValidacaoException("perfilDemandaId", "Perfil de demanda não encontrado.");
            if (p.StoreId != req.StoreId) throw new ValidacaoException("perfilDemandaId", "O perfil de demanda é de outra loja.");
            var curva = PerfilMapper.Curva(p);
            var fatores = req.FatoresDiaSemana is { Count: 7 } f ? f : PerfilMapper.Fatores(p);
            return (PerfilMapper.Resumo(p, curva), curva, fatores);
        }
        if (req.Curva is { Count: > 0 } c)
        {
            if (CurvaDemanda.Validar(c) is { } erro) throw new ValidacaoException("curva", erro);
            return (null, c, req.FatoresDiaSemana is { Count: 7 } f ? f : null);
        }
        throw new ValidacaoException("perfilDemandaId", "Escolha um perfil de demanda ou informe a curva por hora.");
    }

    public sealed record Contexto(
        EntradaCalculo Entrada, ParametrosResolvidos Parametros, List<ParametroUsado> Origens, List<string> Avisos, LojaResumo? Loja,
        EscalaInfo? Escala, Dictionary<string, string?> Servicos);

    /// <summary>Resolve todos os parâmetros (sem a demanda), consultando Store, Compliance, Employee e Scheduling.</summary>
    public async Task<Contexto> ResolverAsync(SimulacaoRequest req, CancellationToken ct)
    {
        Validar(req);
        var avisos = new List<string>();
        var origens = new List<ParametroUsado>();
        var servicos = new Dictionary<string, string?>();
        var inicio = req.PeriodoInicio ?? ProximaSegunda(DateOnly.FromDateTime(DateTime.Today));
        var fim = req.PeriodoFim ?? inicio.AddDays(6);
        if (fim < inicio) throw new ValidacaoException("periodoFim", "O fim do período é anterior ao início.");
        if (fim.DayNumber - inicio.DayNumber > 92) throw new ValidacaoException("periodoFim", "Período de no máximo 93 dias.");

        // Consultas em paralelo (cada uma com timeout curto; falha vira aviso)
        var lojaTask = clients.ObterLojaAsync(req.StoreId, req.SectorId, ct);
        var regrasTask = clients.ObterRegrasAsync(inicio, fim, ct);
        var colabTask = clients.ObterColaboradoresAsync(req.StoreId, inicio, fim, ct);
        var escalaTask = req.EscalaId is { } eid ? clients.ObterEscalaAsync(eid, ct) : Task.FromResult(new Consulta<EscalaInfo>(null, null));
        await Task.WhenAll(lojaTask, regrasTask, colabTask, escalaTask);
        var loja = lojaTask.Result;
        var regras = regrasTask.Result;
        var colab = colabTask.Result;
        var escala = escalaTask.Result;
        servicos["store"] = loja.Erro;
        servicos["compliance"] = regras.Erro;
        servicos["employee"] = colab.Erro;
        if (req.EscalaId is not null) servicos["scheduling"] = escala.Erro;

        // ---------- Operação da loja ----------
        var op = loja.Valor?.Operacao;
        if (loja.Erro is not null)
            avisos.Add($"Não foi possível ler a operação da loja — {loja.Erro} Usados os parâmetros do cenário ou os padrões (07:00–22:00, cobertura mínima 2).");
        else if (op is null)
            avisos.Add("A loja ainda não tem a configuração de operação (abertura, fechamento, cobertura mínima): usados os parâmetros do cenário ou os padrões (07:00–22:00, cobertura mínima 2).");
        if (req.SectorId is { } sid && loja.Valor is { } lv && lv.Setores.All(s => s.Id != sid))
            throw new ValidacaoException("sectorId", "O setor não pertence a esta loja.");

        T Escolher<T>(string chave, string nome, T? doCenario, T? daOperacao, T padrao, Func<T, string> texto, string origemPadrao = Padrao) where T : struct
        {
            var (valor, origem) = doCenario is { } c ? (c, "cenário") : daOperacao is { } o ? (o, op!.Origem) : (padrao, origemPadrao);
            origens.Add(new ParametroUsado(chave, nome, texto(valor), origem));
            return valor;
        }
        string Hm(TimeOnly t) => t.ToString("HH:mm", CultureInfo.InvariantCulture);
        string Min(int m) => $"{m} min";

        var abertura = Escolher("abertura", "Abertura", Hora(req.Abertura, "abertura"), op?.Abertura, new TimeOnly(7, 0), Hm);
        var fechamento = Escolher("fechamento", "Fechamento", Hora(req.Fechamento, "fechamento"), op?.Fechamento, new TimeOnly(22, 0), Hm);
        var cobertura = Escolher("coberturaMinima", "Cobertura mínima", req.CoberturaMinima, op?.CoberturaMinima, 2, v => $"{v} pessoa(s)");
        var minAbertura = Escolher("minimoAbertura", "Mínimo na abertura", req.MinimoAbertura, op?.MinimoAbertura, cobertura, v => $"{v} pessoa(s)", "cobertura mínima");
        var minFechamento = Escolher("minimoFechamento", "Mínimo no fechamento", req.MinimoFechamento, op?.MinimoFechamento, cobertura, v => $"{v} pessoa(s)", "cobertura mínima");
        var incremento = Escolher("incrementoFlexivelMinutos", "Incremento dos flexíveis", req.IncrementoFlexivelMinutos, op?.IncrementoFlexivelMinutos, 30, Min);
        var flexCedo = Hora(req.EntradaFlexivelMaisCedo, "entradaFlexivelMaisCedo") ?? op?.EntradaFlexivelMaisCedo;
        var flexTarde = Hora(req.EntradaFlexivelMaisTarde, "entradaFlexivelMaisTarde") ?? op?.EntradaFlexivelMaisTarde;
        if (fechamento <= abertura) throw new ValidacaoException("fechamento", "O fechamento precisa ser depois da abertura no mesmo dia.");

        // ---------- Regras vigentes no início do período ----------
        if (regras.Erro is not null)
            avisos.Add($"Regras de conformidade indisponíveis — {regras.Erro} Usados os padrões: 44 h/semana (6x1), intervalo de 60 min, interjornada de 11 h, folga em 1 domingo a cada 3 semanas.");
        decimal? Regra(string chave) => regras.Valor?
            .Where(r => r.Chave == chave && r.Status == RuleStatus.Ativo && r.Contem(inicio))
            .OrderByDescending(r => r.VigenciaInicio ?? DateOnly.MinValue)
            .Select(r => (decimal?)r.Valor).FirstOrDefault();
        string OrigemRegra(string chave) => regras.Erro is not null ? Padrao : Regra(chave) is null ? $"{Padrao} (regra {chave} sem valor ativo)" : $"regra {chave}";

        double horasRegra;
        string regime;
        int diasRegra;
        if (regras.Valor is null)
        {
            (horasRegra, regime, diasRegra) = (44, "6x1", 6);
        }
        else
        {
            horasRegra = (double)(Regra(RuleKeys.CargaHorariaSemanal) ?? 44m);
            var limiar = Regra(RuleKeys.CargaMinimaRegime6x1);
            regime = limiar is { } l && (decimal)horasRegra >= l ? "6x1" : "5x2";
            var folgasRegime = Regra(regime == "6x1" ? RuleKeys.FolgasSemana6x1 : RuleKeys.FolgasSemana5x2);
            var minimas = Regra(RuleKeys.FolgasMinimasSemana);
            var folgas = folgasRegime is null && minimas is null ? (regime == "6x1" ? 1 : 2) : (int)Math.Max(folgasRegime ?? 0, minimas ?? 0);
            diasRegra = Math.Clamp(7 - folgas, 1, 7);
        }
        var horasSemanais = req.HorasSemanais ?? horasRegra;
        origens.Add(new ParametroUsado("horasSemanais", "Carga semanal por colaborador", $"{horasSemanais:0.##} h",
            req.HorasSemanais is null ? OrigemRegra(RuleKeys.CargaHorariaSemanal) : "cenário"));
        var dias = req.DiasTrabalhoSemana ?? diasRegra;
        if (req.DiasTrabalhoSemana is not null) regime = $"{dias}x{7 - dias}";
        origens.Add(new ParametroUsado("diasTrabalhoSemana", "Dias de trabalho por semana", $"{dias} ({regime})",
            req.DiasTrabalhoSemana is null ? (regras.Erro is null ? "regras do regime (carga semanal vigente)" : Padrao) : "cenário"));

        // Turnos automáticos da operação: jornada e intervalo pela MESMA conta da escala (carga semanal ÷ dias do regime,
        // limite diário com a compensação do 5x2, regras de intervalo — PlanningContext.ForOperation); o cenário ainda pode fixar
        var automatica = op?.TurnosAutomaticos == true && regras.Valor is { } regrasPeriodo
            ? JornadaAutomatica(abertura, fechamento, incremento, regrasPeriodo, inicio, fim, loja.Valor?.AcordoJornada ?? false)
            : null;
        if (automatica is { } ja && req.EntradaFlexivelMaisCedo is null && req.EntradaFlexivelMaisTarde is null)
        {
            // Intermediários como os da escala: da abertura + incremento até fechamento − duração − incremento
            flexCedo = null;
            flexTarde = fechamento.AddMinutes(-(ja.Journey.Duration + incremento));
        }

        var intervaloRegra = (int)(Regra(RuleKeys.IntervaloMinimo) ?? 60m);
        var intervalo = req.IntervaloMinutos ?? automatica?.Journey.Break ?? op?.IntervaloPadraoMinutos ?? intervaloRegra;
        origens.Add(new ParametroUsado("intervaloMinutos", "Intervalo de refeição", Min(intervalo),
            req.IntervaloMinutos is not null ? "cenário"
            : automatica is not null ? "turnos automáticos (regras de intervalo)"
            : op?.IntervaloPadraoMinutos is not null ? op.Origem : OrigemRegra(RuleKeys.IntervaloMinimo)));
        string Duracao(int v) => $"{v / 60}h{v % 60:00} ({(v - intervalo) / 60}h{(v - intervalo) % 60:00} pagas)";
        int duracao;
        if (req.DuracaoTurnoMinutos is null && automatica is { } auto)
        {
            duracao = auto.Journey.Duration;
            origens.Add(new ParametroUsado("duracaoTurnoMinutos", "Duração do turno (com intervalo)", Duracao(duracao), $"turnos automáticos ({auto.Base})"));
        }
        else
        {
            duracao = Escolher("duracaoTurnoMinutos", "Duração do turno (com intervalo)", req.DuracaoTurnoMinutos,
                op?.TurnosAutomaticos == true ? null : op?.DuracaoJornadaMinutos, 500, Duracao, "padrão (turnos atuais 07:00–15:20 e 13:40–22:00)");
        }
        if (duracao - intervalo <= 0) throw new ValidacaoException("duracaoTurnoMinutos", "A duração do turno precisa ser maior que o intervalo.");

        var regrasIntervalo = new RegrasIntervalo(
            (int)(Regra(RuleKeys.IntervaloInicioMinimoAposEntrada) ?? 60m),
            (int)(Regra(RuleKeys.IntervaloFimMinimoAntesSaida) ?? 60m),
            (int)(Regra(RuleKeys.JornadaContinuaMaxima) ?? 360m));
        var janelaDomingo = Regra(RuleKeys.DomingoFolgaJanela) is { } jd ? (int?)(int)jd : regras.Valor is null ? 3 : null;
        origens.Add(new ParametroUsado("janelaDomingo", "Folga aos domingos", janelaDomingo is { } j ? $"1 a cada {j} semanas" : "sem limite",
            OrigemRegra(RuleKeys.DomingoFolgaJanela)));
        var interjornada = (int)(Regra(RuleKeys.InterjornadaMinima) ?? 660m);
        origens.Add(new ParametroUsado("interjornada", "Interjornada mínima", $"{interjornada / 60.0:0.#} h", OrigemRegra(RuleKeys.InterjornadaMinima)));

        // ---------- Turnos ----------
        var modo = req.ModoTurnos ?? ModoTurnos.Operacao;
        List<TurnoCadastrado>? cadastrados = null;
        if (modo == ModoTurnos.Cadastrados)
        {
            cadastrados = loja.Valor?.Turnos.Where(t => t.Ativo && t.Fim > t.Inicio)
                .Select(t => new TurnoCadastrado(t.Codigo, Minutos(t.Inicio), Minutos(t.Fim), t.IntervaloMinutos)).ToList();
            if (cadastrados is not { Count: > 0 })
            {
                avisos.Add(loja.Erro is not null
                    ? "Turnos cadastrados indisponíveis (Store.Api fora do ar): usados abertura, fechamento e flexíveis."
                    : "A loja não tem turnos ativos cadastrados: usados abertura, fechamento e flexíveis.");
                modo = ModoTurnos.Operacao;
            }
        }
        origens.Add(new ParametroUsado("modoTurnos", "Turnos permitidos", modo switch
        {
            ModoTurnos.Fixos => "só abertura e fechamento",
            ModoTurnos.Cadastrados => $"cadastrados na loja ({string.Join(", ", cadastrados!.Select(c => c.Codigo))})",
            _ => $"abertura, fechamento e flexíveis a cada {incremento} min"
        }, req.ModoTurnos is null ? Padrao : "cenário"));

        // ---------- Disponíveis ----------
        DisponibilidadeEntrada? disponibilidade = null;
        if (colab.Valor is { } lista)
        {
            var doSetor = req.SectorId is { } s ? lista.Where(c => c.SectorIds.Contains(s)).ToList() : lista;
            var datas = Enumerable.Range(0, fim.DayNumber - inicio.DayNumber + 1).Select(inicio.AddDays).ToList();
            var porDia = new int[7];
            for (var d = 0; d < 7; d++)
            {
                var dia = CurvaDemanda.Semana[d];
                var doDia = datas.Where(x => x.DayOfWeek == dia).ToList();
                porDia[d] = doDia.Count == 0
                    ? doSetor.Count
                    : (int)Math.Floor(doDia.Average(x => doSetor.Count(c => !c.UnavailableDates.Contains(x))) + 1e-9);
            }
            var comDisponibilidade = doSetor.Count(c => datas.Any(x => !c.UnavailableDates.Contains(x)));
            disponibilidade = new DisponibilidadeEntrada(comDisponibilidade, porDia);
            if (comDisponibilidade < doSetor.Count)
                avisos.Add($"{doSetor.Count - comDisponibilidade} colaborador(es) ausente(s) em todo o período {inicio:dd/MM}–{fim:dd/MM} (férias/afastamento) não entram nos disponíveis.");
            var perfis = doSetor.Where(c => !string.IsNullOrEmpty(c.PerfilJornada)).GroupBy(c => c.PerfilJornada!).ToList();
            if (perfis.Count > 0)
                avisos.Add("Perfis de jornada no cadastro: " + string.Join(", ", perfis.Select(g => $"{g.Count()} {g.Key}")) + ".");
        }
        else if (colab.Erro is not null)
        {
            avisos.Add($"Colaboradores indisponíveis — {colab.Erro} Sem os disponíveis, déficit e contratações; a necessidade e o quadro mínimo continuam calculados.");
        }

        // ---------- Escala escolhida ----------
        double[][]? presentes = null;
        EscalaInfo? escalaInfo = null;
        if (req.EscalaId is not null)
        {
            if (escala.Valor is { } e)
            {
                if (e.StoreId != req.StoreId) throw new ValidacaoException("escalaId", "A escala é de outra loja.");
                escalaInfo = e;
                presentes = PresentesPorHora(e, req.SectorId);
            }
            else
            {
                avisos.Add($"Escala não consultada — {escala.Erro} A comparação com a escala foi omitida.");
            }
        }

        // ---------- Picos manuais da loja ----------
        var (picoManual, picoAdicional) = PicosPorHora(loja.Valor?.Picos, req.SectorId, inicio, fim);
        if (picoManual is not null)
        {
            var usados = loja.Valor!.Picos!.Where(p => p.SectorId is null || p.SectorId == req.SectorId)
                .Where(p => p.Data is null || (p.Data >= inicio && p.Data <= fim)).Select(p => p.Rotulo).Distinct().ToList();
            avisos.Add($"Picos manuais da loja considerados (substituem a necessidade calculada na hora): {string.Join("; ", usados.Take(8))}" +
                       $"{(usados.Count > 8 ? "..." : "")}.");
            origens.Add(new ParametroUsado("picosManuais", "Picos manuais", $"{usados.Count} pico(s)", "cadastro da loja"));
        }

        var meta = req.MetaCobertura ?? 1.0;
        var tempo = Math.Clamp(req.TempoLimiteSegundos ?? config.GetValue("Dimensionamento:TempoLimiteSegundos", 20.0), 2, 120);
        var entrada = new EntradaCalculo
        {
            Demanda = [], // preenchida pelo chamador
            TmaSegundos = req.TmaSegundos,
            Utilizacao = req.Utilizacao,
            NivelServicoMeta = req.NivelServicoMeta,
            NivelServicoTempoSegundos = req.NivelServicoMeta is null ? null : req.NivelServicoTempoSegundos ?? 30,
            Shrinkage = req.Shrinkage,
            CoberturaMinima = cobertura,
            MinimoAbertura = minAbertura,
            MinimoFechamento = minFechamento,
            Abertura = Minutos(abertura),
            Fechamento = Minutos(fechamento),
            HorasSemanais = horasSemanais,
            DiasTrabalho = dias,
            DuracaoTurno = duracao,
            Intervalo = intervalo,
            Incremento = incremento,
            FlexMaisCedo = flexCedo is { } fc ? Minutos(fc) : null,
            FlexMaisTarde = flexTarde is { } ft ? Minutos(ft) : null,
            ModoTurnos = modo,
            TurnosCadastrados = cadastrados,
            RegrasIntervalo = regrasIntervalo,
            JanelaDomingo = janelaDomingo,
            Interjornada = interjornada,
            MetaCobertura = meta,
            PicoManual = picoManual,
            PicoAdicional = picoAdicional,
            Disponibilidade = disponibilidade,
            PresentesEscala = presentes,
            SensibilidadeTma = req.SensibilidadeTma,
            SensibilidadeUtilizacao = req.SensibilidadeUtilizacao,
            SensibilidadeCobertura = req.SensibilidadeCobertura,
            SensibilidadeTemporal = req.SensibilidadeTemporal ?? true,
            TempoLimite = tempo,
            TempoLimiteSensibilidade = Math.Min(tempo, config.GetValue("Dimensionamento:TempoLimiteSensibilidadeSegundos", 6.0)),
            Workers = config.GetValue("Dimensionamento:SolverWorkers", 8),
            Paralelo = config.GetValue("Dimensionamento:SensibilidadeEmParalelo", 3),
        };

        var parametros = new ParametrosResolvidos(Hm(abertura), Hm(fechamento), cobertura, minAbertura, minFechamento, horasSemanais, dias, regime,
            duracao, intervalo, incremento, flexCedo is { } a ? Hm(a) : null, flexTarde is { } b ? Hm(b) : null, modo, janelaDomingo, interjornada,
            inicio, fim);
        var lojaResumo = loja.Valor is { } lj ? new LojaResumo(lj.Id, lj.Codigo, lj.Nome, lj.Setores, lj.Operacao is not null) : null;
        logger.LogInformation("Cenário da loja {StoreId}: TMA {Tma}s, utilização {Util}, mínimo {Min}, regime {Regime}, turnos {Modo}",
            req.StoreId, req.TmaSegundos, req.Utilizacao, cobertura, regime, modo);
        return new Contexto(entrada, parametros, origens, avisos, lojaResumo, escalaInfo, servicos);
    }

    /// <summary>
    /// Presentes por dia da semana e hora numa escala: minutos trabalhados (fora do intervalo) em cada hora ÷ 60, somados por
    /// data e com média entre as datas do mesmo dia da semana. Intervalo não posicionado: no meio do turno.
    /// </summary>
    public static double[][] PresentesPorHora(EscalaInfo e, Guid? setor)
    {
        var porData = new Dictionary<DateOnly, double[]>();
        foreach (var c in e.Celulas)
        {
            if (!porData.ContainsKey(c.Date)) porData[c.Date] = new double[24];
            if (c.Folga || c.Start is null || c.End is null) continue;
            var ini = Minutos(c.Start.Value);
            var fim = Minutos(c.End.Value);
            if (fim <= ini) fim += 1440;
            var brk = c.BreakMinutes ?? (c.ShiftId is { } sh && e.IntervaloPorTurno.TryGetValue(sh, out var b) ? b : 0);
            var brkIni = c.BreakStart is { } bs ? Minutos(bs) : ini + Math.Max(0, (fim - ini - brk) / 2);
            if (brkIni < ini) brkIni += 1440;
            var horas = porData[c.Date];
            for (var h = 0; h < 24; h++)
            {
                var de = h * 60;
                var ate = de + 60;
                var trabalhado = Sobreposicao(ini, fim, de, ate) - Sobreposicao(brkIni, brkIni + brk, de, ate);
                horas[h] += Math.Max(0, trabalhado) / 60.0;
            }
        }
        var r = new double[7][];
        for (var d = 0; d < 7; d++)
        {
            var dia = CurvaDemanda.Semana[d];
            var datas = Enumerable.Range(0, e.EndDate.DayNumber - e.StartDate.DayNumber + 1).Select(e.StartDate.AddDays).Where(x => x.DayOfWeek == dia).ToList();
            r[d] = new double[24];
            if (datas.Count == 0) continue;
            for (var h = 0; h < 24; h++) r[d][h] = datas.Average(x => porData.TryGetValue(x, out var v) ? v[h] : 0);
        }
        return r;
    }

    private static int Sobreposicao(int a1, int a2, int b1, int b2) => Math.Max(0, Math.Min(a2, b2) - Math.Max(a1, b1));

    /// <summary>
    /// Picos manuais por dia da semana (0..6 = seg..dom) e hora: pessoas desejadas e adicional (o maior entre os picos que
    /// tocam a hora). Vale o pico do setor ou da loja inteira; o de uma data entra no dia da semana dela quando a data está
    /// no período. (null, null) = nenhum pico.
    /// </summary>
    public static (int[][]? Quantidade, int[][]? Adicional) PicosPorHora(IReadOnlyList<PicoLoja>? picos, Guid? setor, DateOnly inicio, DateOnly fim)
    {
        var lista = (picos ?? []).Where(p => p.SectorId is null || setor is null || p.SectorId == setor)
            .Where(p => p.Data is null ? p.DiaSemana is not null : p.Data >= inicio && p.Data <= fim)
            .ToList();
        if (lista.Count == 0) return (null, null);
        var qtd = Enumerable.Range(0, 7).Select(_ => new int[24]).ToArray();
        var adic = Enumerable.Range(0, 7).Select(_ => new int[24]).ToArray();
        foreach (var p in lista)
        {
            var d = CurvaDemanda.IndiceDia(p.Data?.DayOfWeek ?? p.DiaSemana!.Value);
            var (de, ate) = (Minutos(p.Inicio), Minutos(p.Fim));
            for (var h = 0; h < 24; h++)
            {
                if (Sobreposicao(de, ate, h * 60, h * 60 + 60) <= 0) continue;
                qtd[d][h] = Math.Max(qtd[d][h], p.Quantidade ?? 0);
                adic[d][h] = Math.Max(adic[d][h], p.Adicional ?? 0);
            }
        }
        return (qtd, adic);
    }

    public static int Minutos(TimeOnly t) => t.Hour * 60 + t.Minute;

    /// <summary>
    /// Jornada dos turnos automáticos no período (a mais frequente entre os dias de segunda a sábado), com o expediente
    /// <paramref name="abertura"/>–<paramref name="fechamento"/> em todos os dias e as regras vigentes: a mesma conta da
    /// geração da escala (PlanningContext.ForOperation / DayJourneys). null = nenhuma jornada cabe.
    /// </summary>
    public static (DayJourney Journey, string Base)? JornadaAutomatica(TimeOnly abertura, TimeOnly fechamento, int incremento,
        IReadOnlyList<ComplianceRuleSnapshot> regras, DateOnly inicio, DateOnly fim, bool acordoJornada)
    {
        if (fechamento <= abertura || fim < inicio) return null;
        var op = new OperationSnapshot(abertura, fechamento, 1, 1, 1, incremento, null, null, OperationPlan.DuracaoPadraoMinutos, null,
            TurnosAutomaticos: true);
        var ctx = PlanningContext.ForOperation(op, Guid.Empty, [], regras, [], inicio, fim, acordoJornada);
        var dias = Enumerable.Range(ctx.FirstDay, ctx.PeriodDays).Where(d => !ctx.IsSunday(d) && ctx.JourneyAt(d) is not null).ToList();
        if (dias.Count == 0) return null;
        var maisFrequente = dias.GroupBy(d => ctx.JourneyAt(d)!.Value).OrderByDescending(g => g.Count()).ThenBy(g => g.Min()).First();
        return (maisFrequente.Key, ctx.JourneyBasis(maisFrequente.First()));
    }

    private static TimeOnly? Hora(string? texto, string campo)
    {
        if (string.IsNullOrWhiteSpace(texto)) return null;
        return EscalaClients.Hora(texto) ?? throw new ValidacaoException(campo, $"Horário inválido: \"{texto}\" (use HH:mm).");
    }

    private static void Validar(SimulacaoRequest r)
    {
        if (r.StoreId == Guid.Empty) throw new ValidacaoException("storeId", "Informe a loja.");
        if (r.TmaSegundos is <= 0 or > 3600) throw new ValidacaoException("tmaSegundos", "O TMA deve estar entre 1 e 3600 segundos.");
        if (r.Utilizacao is <= 0 or > 1) throw new ValidacaoException("utilizacao", "A utilização alvo deve estar entre 1% e 100%.");
        if (r.Shrinkage is < 0 or >= 0.9) throw new ValidacaoException("shrinkage", "O shrinkage deve estar entre 0% e 89%.");
        if (r.NivelServicoMeta is { } m && m is <= 0 or >= 1) throw new ValidacaoException("nivelServicoMeta", "A meta do nível de serviço deve estar entre 1% e 99,9%.");
        if (r.NivelServicoTempoSegundos is { } t && t is <= 0 or > 3600) throw new ValidacaoException("nivelServicoTempoSegundos", "O tempo de espera alvo deve estar entre 1 e 3600 s.");
        if (r.CoberturaMinima is < 0 or > 50) throw new ValidacaoException("coberturaMinima", "Cobertura mínima entre 0 e 50.");
        if (r.MinimoAbertura is < 0 or > 50) throw new ValidacaoException("minimoAbertura", "Mínimo na abertura entre 0 e 50.");
        if (r.MinimoFechamento is < 0 or > 50) throw new ValidacaoException("minimoFechamento", "Mínimo no fechamento entre 0 e 50.");
        if (r.HorasSemanais is <= 0 or > 60) throw new ValidacaoException("horasSemanais", "Carga semanal entre 1 e 60 horas.");
        if (r.DiasTrabalhoSemana is < 1 or > 6) throw new ValidacaoException("diasTrabalhoSemana", "Dias de trabalho por semana entre 1 e 6.");
        if (r.DuracaoTurnoMinutos is < 60 or > 720) throw new ValidacaoException("duracaoTurnoMinutos", "Duração do turno entre 60 e 720 minutos.");
        if (r.IntervaloMinutos is < 0 or > 240) throw new ValidacaoException("intervaloMinutos", "Intervalo entre 0 e 240 minutos.");
        if (r.IncrementoFlexivelMinutos is < 5 or > 120) throw new ValidacaoException("incrementoFlexivelMinutos", "Incremento entre 5 e 120 minutos.");
        if (r.MetaCobertura is <= 0 or > 1) throw new ValidacaoException("metaCobertura", "A meta de horas cobertas deve estar entre 1% e 100%.");
        if (r.ModoTurnos is { } modo && !ModoTurnos.Todos.Contains(modo)) throw new ValidacaoException("modoTurnos", "Turnos: Operacao, Fixos ou Cadastrados.");
        if (r.FatoresDiaSemana is { } f && (f.Count != 7 || f.Any(v => v < 0 || v > 10)))
            throw new ValidacaoException("fatoresDiaSemana", "Informe 7 fatores (segunda a domingo) entre 0 e 10.");
        if (r.SensibilidadeTma is { Count: > 12 } || r.SensibilidadeUtilizacao is { Count: > 12 } || r.SensibilidadeCobertura is { Count: > 12 })
            throw new ValidacaoException("sensibilidade", "No máximo 12 valores em cada simulação de sensibilidade.");
    }
}

/// <summary>Conversões do perfil de demanda (JSON gravado ↔ curva).</summary>
public static class PerfilMapper
{
    public static List<PontoCurva> Curva(PerfilDemanda p) => JsonSerializer.Deserialize<List<PontoCurva>>(p.CurvaJson, Json.Options) ?? [];

    public static List<double>? Fatores(PerfilDemanda p) =>
        p.FatoresDiaJson is null ? null : JsonSerializer.Deserialize<List<double>>(p.FatoresDiaJson, Json.Options);

    public static List<TotalCheckout>? Checkouts(PerfilDemanda p) =>
        p.CheckoutsJson is null ? null : JsonSerializer.Deserialize<List<TotalCheckout>>(p.CheckoutsJson, Json.Options);

    /// <summary>Documentos por dia (média dos 7 dias da semana).</summary>
    public static double MediaDia(List<PontoCurva> curva, List<double>? fatores) =>
        Math.Round(CurvaDemanda.Matriz(curva, fatores).Average(d => d.Sum()), 2);

    public static PerfilResumo Resumo(PerfilDemanda p, List<PontoCurva> curva) =>
        new(p.Id, p.Nome, p.Origem, p.Dias, p.TotalDocumentos, MediaDia(curva, Fatores(p)));

    /// <summary>Média por hora (dos 7 dias). 3 casas: a média do relatório (1 casa) não sofre arredondamento duplo (131,852 → 131,9).</summary>
    public static double[] MediaPorHora(List<PontoCurva> curva, List<double>? fatores)
    {
        var m = CurvaDemanda.Matriz(curva, fatores);
        return Enumerable.Range(0, 24).Select(h => Math.Round(m.Average(d => d[h]), 3)).ToArray();
    }

    public static PerfilListItem Item(PerfilDemanda p)
    {
        var curva = Curva(p);
        var fatores = Fatores(p);
        return new PerfilListItem(p.Id, p.StoreId, p.SectorId, p.Nome, p.Origem, p.Observacao, p.PeriodoInicio, p.PeriodoFim, p.Dias,
            p.TotalDocumentos, MediaDia(curva, fatores), curva.Select(c => c.Hora).Distinct().Count(), curva.Any(c => c.DiaSemana is not null),
            p.CriadoPor, p.CriadoEm, p.AtualizadoPor, p.AtualizadoEm, MediaPorHora(curva, fatores));
    }

    public static PerfilDetalhe Detalhe(PerfilDemanda p)
    {
        var curva = Curva(p);
        var fatores = Fatores(p);
        var media = MediaPorHora(curva, fatores);
        return new PerfilDetalhe(Item(p),curva.OrderBy(c => c.DiaSemana is null ? -1 : CurvaDemanda.IndiceDia(c.DiaSemana.Value)).ThenBy(c => c.Hora).ToList(),
            fatores, Checkouts(p), media);
    }
}

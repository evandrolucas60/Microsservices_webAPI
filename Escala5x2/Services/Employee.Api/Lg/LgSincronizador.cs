using System.Net.Http.Json;
using Employee.Api.Data;
using Employee.Api.Domain;
using Escala5x2.BuildingBlocks.Contracts.Employees;
using Escala5x2.BuildingBlocks.Contracts.Http;
using Escala5x2.BuildingBlocks.Contracts.Planning;
using Escala5x2.BuildingBlocks.Contracts.Stores;
using Escala5x2.BuildingBlocks.Shared.Auth;
using Escala5x2.BuildingBlocks.Shared.Lg;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Employee.Api.Lg;

/// <summary>Setores dos órgãos responsáveis da LG numa loja (Store.Api, POST /internal/stores/{id}/sectors/garantir-lg).</summary>
public interface ISetoresLgLoja
{
    /// <summary>
    /// Garante um setor por órgão (cria/adota sem duplicar) e devolve código do órgão → SectorId. Store.Api fora do ar ou
    /// recusando: <see cref="LgIntegracaoException"/> (a sincronização falha antes de gravar os colaboradores).
    /// </summary>
    Task<GarantirSetoresLgResponse> GarantirAsync(Guid storeId, IReadOnlyList<LgOrgaoSetor> orgaos, string por, CancellationToken ct);
}

public sealed class SetoresLgLojaHttp(IHttpClientFactory factory, ILogger<SetoresLgLojaHttp> logger) : ISetoresLgLoja
{
    public const string Cliente = "store";

    public async Task<GarantirSetoresLgResponse> GarantirAsync(Guid storeId, IReadOnlyList<LgOrgaoSetor> orgaos, string por, CancellationToken ct)
    {
        try
        {
            using var response = await factory.CreateClient(Cliente).PostAsJsonAsync(
                $"/internal/stores/{storeId}/sectors/garantir-lg", new GarantirSetoresLgRequest(orgaos, por), ct);
            if (!response.IsSuccessStatusCode)
            {
                var corpo = await response.Content.ReadAsStringAsync(ct);
                throw new LgIntegracaoException(
                    $"O Store.Api recusou os setores da LG da loja {storeId} (HTTP {(int)response.StatusCode}{(corpo.Length is > 0 and < 300 ? $": {corpo}" : "")}); nada foi alterado nos colaboradores.",
                    transitorio: (int)response.StatusCode >= 500);
            }
            return await response.Content.ReadFromJsonAsync<GarantirSetoresLgResponse>(ct)
                   ?? throw new LgIntegracaoException($"O Store.Api não devolveu os setores da loja {storeId}; nada foi alterado nos colaboradores.", transitorio: true);
        }
        catch (Exception ex) when (ex is not LgIntegracaoException && !ct.IsCancellationRequested)
        {
            logger.LogWarning("LG: setores da loja {StoreId} indisponíveis no Store.Api ({Motivo})", storeId, $"{ex.GetType().Name}: {ex.Message}");
            throw new LgIntegracaoException(
                $"Não foi possível garantir os setores da LG no Store.Api ({ex.GetType().Name}: {ex.Message}); nada foi alterado nos colaboradores. Tente novamente.",
                transitorio: true, ex);
        }
    }
}

/// <summary>Sincronização de lojas no Store.Api antes da de colaboradores (LG:Lojas:SincronizarAntesDosColaboradores).</summary>
public interface ISincronizacaoLojasLg
{
    /// <summary>Executa e devolve o aviso para o resumo dos colaboradores (resultado ou motivo da falha). Nunca lança, exceto cancelamento.</summary>
    Task<string> SincronizarAsync(string por, CancellationToken ct);
}

/// <summary>POST /internal/stores/sincronizar-lg do Store.Api (X-Internal-Key via InternalKeyHandler), aguardando o fim.</summary>
public sealed class SincronizacaoLojasLgHttp(IHttpClientFactory factory, ILogger<SincronizacaoLojasLgHttp> logger) : ISincronizacaoLojasLg
{
    public const string Cliente = "store-lg";

    public async Task<string> SincronizarAsync(string por, CancellationToken ct)
    {
        try
        {
            using var response = await factory.CreateClient(Cliente).PostAsJsonAsync("/internal/stores/sincronizar-lg", new { Por = por }, ct);
            if (!response.IsSuccessStatusCode)
            {
                var detalhe = await response.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>(ct).ConfigureAwait(false);
                return $"Lojas não sincronizadas antes dos colaboradores (HTTP {(int)response.StatusCode}: {detalhe?.Detail ?? detalhe?.Title ?? response.ReasonPhrase}); usadas as lojas já cadastradas.";
            }
            var r = await response.Content.ReadFromJsonAsync<LgLojasResumo>(ct);
            if (r is null) return "Lojas: o Store.Api não devolveu o resumo da sincronização; usadas as lojas já cadastradas.";
            if (r.Status != "Concluida") return $"Lojas não sincronizadas antes dos colaboradores: {r.Mensagem}; usadas as lojas já cadastradas.";
            return $"Lojas sincronizadas com a LG antes: {r.Criadas} criada(s), {r.Atualizadas} atualizada(s), {r.Inativadas} inativada(s), " +
                   $"{r.Reativadas} reativada(s), {r.Conflitos.Count} conflito(s).";
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("LG: sincronização de lojas no Store.Api falhou ({Motivo}); seguem as lojas já cadastradas", $"{ex.GetType().Name}: {ex.Message}");
            return $"Lojas não sincronizadas antes dos colaboradores ({ex.GetType().Name}: {ex.Message}); usadas as lojas já cadastradas.";
        }
    }
}

/// <summary>Centro de custo sem loja reconhecida (quantidade e até 20 matrículas, sem dados pessoais).</summary>
public sealed record LgCentroIgnorado(string? Codigo, string? Descricao, int Quantidade, IReadOnlyList<string> Matriculas);

/// <summary>Setores dos órgãos responsáveis numa loja: quantos órgãos vieram e quantos setores foram criados/adotados/renomeados.</summary>
public sealed record LgSetoresLojaResumo(Guid StoreId, string Loja, int Orgaos, int Criados, int Adotados, int Renomeados);

/// <summary>
/// Resumo de uma execução da sincronização (sem CPF/data de nascimento). <paramref name="Setores"/>: setores dos órgãos
/// responsáveis por loja (só lojas com algo criado/adotado/renomeado); <paramref name="SetoresDivergentes"/>: colaboradores
/// com setor alterado manualmente diferente do setor da LG.
/// </summary>
public sealed record LgResumo(
    string Status, DateOnly DataVigente, DateTime IniciadaEm, DateTime ConcluidaEm, string Por,
    int Paginas, int TotalLg, int Criados, int Atualizados, int Inalterados, int Ignorados, int Inativados, int Movidos,
    int Reativados, int SemSetor, int PendenciasDePeriodo, int Erros,
    IReadOnlyList<LgCentroIgnorado> CentrosIgnorados, IReadOnlyList<string> Avisos, IReadOnlyList<string> MensagensErro, string? Mensagem,
    int SetoresCriados = 0, int SetoresAdotados = 0, int SetoresDivergentes = 0, IReadOnlyList<LgSetoresLojaResumo>? Setores = null,
    LgFeriasResumo? Ferias = null);

/// <summary>
/// Sincroniza os colaboradores com a LG. Regras:
/// <list type="bullet">
/// <item>Loja: CentroDeCusto.Codigo = código LG da loja (Store.LgCentroDeCustoCodigo, da sincronização de lojas); senão
/// LG:CentrosDeCusto; senão "LOJA nn" da descrição casado com o Code pelo código normalizado (ver
/// <see cref="LgRegras.LojaDoCentroDeCusto"/>). Sem loja → ignorado (contagem e lista no status); centro de custo de loja
/// que ainda não existe no Store.Api gera o aviso "rode a sincronização de lojas".</item>
/// <item>Upsert por (Loja, Matrícula). A mesma pessoa (Empresa + Matrícula) em outra loja = mudança de centro de custo:
/// muda de loja (setores da loja anterior saem) e gera aviso/auditoria.</item>
/// <item>Quem já veio da LG e não veio mais (demitido/transferido): inativado (nunca apagado); volta a ser ativado se reaparecer.</item>
/// <item>Setor: OrgaoResponsavel. Antes de gravar, uma chamada por loja ao Store.Api garante um setor por órgão (cria ou
/// adota pelo nome, sem duplicar). O setor do órgão é o setor padrão; enquanto os setores não forem alterados
/// manualmente, acompanha a LG (troca se o órgão mudar); depois da alteração manual, a LG não sobrescreve (ver
/// <see cref="LgRegras.AplicarSetorLg"/>). Órgão vazio → sem setor.</item>
/// <item>Nome em Title Case, cargo = Cargo.Descricao, situação/DataInicioSituacaoAtual e registro completo (jsonb).</item>
/// <item>Contrato: colaborador novo recebe o padrão (jornada controlada, 1 domingo a cada 3; a carga semanal é a da regra
/// carga_horaria_semanal vigente); contrato existente é preservado.</item>
/// </list>
/// Nada é gravado nos colaboradores se a consulta à LG, às lojas ou aos setores (Store.Api) falhar (a consulta inteira e
/// os setores vêm antes de qualquer alteração; setores já garantidos em outras lojas ficam — são idempotentes).
/// Depois de gravar, consulta as férias de todos os ativos (<see cref="LgFeriasSincronizador"/>, LG:Ferias:AposSincronizarColaboradores):
/// falha nas férias só vira aviso e contagem no resumo (os colaboradores já estão gravados).
/// </summary>
public sealed class LgSincronizador(
    EmployeeDbContext db, LgSoapClient soap, ILojasEscala lojasEscala, ISetoresLgLoja setoresLgLoja, ISincronizacaoLojasLg sincronizacaoLojas,
    IPublishEndpoint bus, IOptionsMonitor<LgOptions> options, ILogger<LgSincronizador> logger, LgFeriasSincronizador? ferias = null)
{
    public const string Autor = "Integração LG";
    private const int MaxMensagens = 50;

    public async Task<LgResumo> SincronizarAsync(string por, Action<string, int, int>? progresso, CancellationToken ct)
    {
        var o = options.CurrentValue;
        var inicio = DateTime.UtcNow;
        var hoje = o.Hoje(inicio);
        if (!o.Configurada) throw new LgIntegracaoException(o.MensagemNaoConfigurada());

        // Opcional (LG:Lojas:SincronizarAntesDosColaboradores): lojas antes, pelo Store.Api (ordem recomendada lojas → colaboradores)
        string? avisoLojas = null;
        if (o.Lojas.SincronizarAntesDosColaboradores)
        {
            progresso?.Invoke("Sincronizando as lojas", 0, 0);
            avisoLojas = await sincronizacaoLojas.SincronizarAsync(por, ct);
        }

        progresso?.Invoke("Consultando a LG", 0, 0);
        var consulta = await soap.ConsultarTodosAsync(hoje, (p, t) => progresso?.Invoke("Consultando a LG", p, t), ct);

        progresso?.Invoke("Consultando as lojas", consulta.Paginas, consulta.Paginas);
        // Sem cache: a sincronização de lojas pode ter acabado de criar/vincular lojas
        var lojas = await lojasEscala.ObterLojasAtualizadasAsync(ct)
            ?? throw new LgIntegracaoException("Não foi possível consultar as lojas no Store.Api; nada foi alterado. Tente novamente.", transitorio: true);
        var nomeLoja = lojas.ToDictionary(l => l.Id, l => $"{l.Code} - {l.Name}");

        // Setores pelos órgãos responsáveis: uma chamada por loja ao Store.Api ANTES de gravar (falha → nada gravado)
        var orgaosPorLoja = OrgaosPorLoja(consulta.Contratos, lojas, o.CentrosDeCusto);
        var setoresLg = new Dictionary<Guid, IReadOnlyDictionary<int, Guid>>();
        var resumoSetores = new List<LgSetoresLojaResumo>();
        var avisosSetores = new List<string>();
        var n = 0;
        foreach (var (lojaId, orgaos) in orgaosPorLoja)
        {
            progresso?.Invoke("Garantindo os setores da LG", ++n, orgaosPorLoja.Count);
            var r = await setoresLgLoja.GarantirAsync(lojaId, orgaos, por, ct);
            setoresLg[lojaId] = r.Setores;
            if (r.Criados + r.Adotados + r.Renomeados > 0)
                resumoSetores.Add(new LgSetoresLojaResumo(lojaId, nomeLoja.GetValueOrDefault(lojaId, lojaId.ToString()), orgaos.Count,
                    r.Criados, r.Adotados, r.Renomeados));
            foreach (var aviso in r.Avisos) avisosSetores.Add($"Loja {nomeLoja.GetValueOrDefault(lojaId, lojaId.ToString())}: {aviso}");
        }

        progresso?.Invoke("Gravando os colaboradores", consulta.Paginas, consulta.Paginas);
        var todos = await db.Employees.Include(e => e.Sectors).Include(e => e.Unavailabilities).AsSplitQuery().ToListAsync(ct);
        var porLoja = todos.GroupBy(e => (e.StoreId, e.Registration)).ToDictionary(g => g.Key, g => g.First());
        var porLg = todos.Where(e => e.LgEmpresaCodigo is not null)
            .GroupBy(e => (Empresa: e.LgEmpresaCodigo!.Value, e.Registration)).ToDictionary(g => g.Key, g => g.ToList());

        var vistos = new HashSet<Guid>();
        var chavesLg = new HashSet<(int, string)>();
        var eventos = new List<EmployeeLgStatusChanged>();
        var ignorados = new Dictionary<string, (LgItem? Centro, List<string> Matriculas)>();
        var avisos = new List<string>();
        if (avisoLojas is not null) avisos.Add(avisoLojas);
        foreach (var aviso in avisosSetores) Adicionar(avisos, aviso);
        var erros = new List<string>();
        int criados = 0, atualizados = 0, inalterados = 0, nIgnorados = 0, inativados = 0, movidos = 0, reativados = 0, nErros = 0;
        var agora = DateTime.UtcNow;

        void EventoStatus(Domain.Employee e, Guid? lojaAnterior, string acao, string motivo) =>
            eventos.Add(new EmployeeLgStatusChanged(e.Id, e.StoreId, lojaAnterior, e.Registration, e.Name, acao, motivo, por, agora));

        void Inativar(Domain.Employee e, string motivo)
        {
            e.Active = false;
            e.LgInativadoEm = agora;
            inativados++;
            EventoStatus(e, null, "inativado", motivo);
        }

        foreach (var c in consulta.Contratos)
        {
            ct.ThrowIfCancellationRequested();
            var matricula = c.Matricula.Trim();
            try
            {
                if (matricula.Length is 0 or > 30)
                {
                    nErros++;
                    Adicionar(erros, $"Registro sem matrícula válida (centro de custo {c.CentroDeCusto?.Codigo}).");
                    continue;
                }
                var empresa = c.EmpresaCodigo ?? o.EmpresasEfetivas[0];
                if (!chavesLg.Add((empresa, matricula)))
                {
                    nErros++;
                    Adicionar(erros, $"Matrícula {matricula} (empresa {empresa}) repetida na resposta da LG: considerado só o primeiro registro.");
                    continue;
                }
                var mesmaPessoa = porLg.GetValueOrDefault((empresa, matricula)) ?? [];

                // Loja: código LG da loja → LG:CentrosDeCusto → "LOJA nn" da descrição (LgRegras.LojaDoCentroDeCusto)
                var (loja, avisoLoja) = LgRegras.LojaDoCentroDeCusto(c.CentroDeCusto, lojas, o.CentrosDeCusto);
                if (loja is null)
                {
                    nIgnorados++;
                    var chave = $"{c.CentroDeCusto?.Codigo}|{c.CentroDeCusto?.Descricao}";
                    if (!ignorados.TryGetValue(chave, out var ig))
                    {
                        ignorados[chave] = ig = (c.CentroDeCusto, []);
                        // Centro de custo de loja que ainda não existe no Store.Api: um aviso por centro de custo
                        if (avisoLoja is not null) Adicionar(avisos, avisoLoja);
                    }
                    ig.Matriculas.Add(matricula);
                    // Já sincronizado antes e agora num centro de custo sem loja (CD, administrativo...): sai da escala
                    foreach (var antigo in mesmaPessoa.Where(x => x.Active))
                    {
                        vistos.Add(antigo.Id);
                        Inativar(antigo, $"centro de custo {c.CentroDeCusto?.Codigo} {c.CentroDeCusto?.Descricao} sem loja na Escala 5x2");
                        Adicionar(avisos, $"Matrícula {matricula} ({antigo.Name}) foi para o centro de custo {c.CentroDeCusto?.Descricao} (sem loja na Escala) e foi inativada.");
                    }
                    continue;
                }

                var e = porLoja.GetValueOrDefault((loja.Id, matricula));
                var novo = false;
                if (e is null && mesmaPessoa.FirstOrDefault(x => x.StoreId != loja.Id && !vistos.Contains(x.Id)) is { } outro)
                {
                    // Mudou de centro de custo: muda de loja; os setores eram da loja anterior
                    var anterior = outro.StoreId;
                    porLoja.Remove((anterior, matricula));
                    outro.StoreId = loja.Id;
                    db.RemoveRange(outro.Sectors);
                    outro.Sectors.Clear();
                    // Setores manuais eram da loja anterior: na loja nova volta a seguir o setor da LG
                    outro.SetoresManuais = false;
                    outro.SetoresAlteradosPor = null;
                    outro.SetoresAlteradosEm = null;
                    porLoja[(loja.Id, matricula)] = outro;
                    movidos++;
                    EventoStatus(outro, anterior, "movido", $"centro de custo {c.CentroDeCusto?.Codigo} {c.CentroDeCusto?.Descricao}");
                    Adicionar(avisos, $"Matrícula {matricula} ({outro.Name}) mudou de loja: {nomeLoja.GetValueOrDefault(anterior, anterior.ToString())} → {nomeLoja[loja.Id]}. Setores da loja anterior removidos.");
                    e = outro;
                }
                if (e is null)
                {
                    novo = true;
                    e = new Domain.Employee
                    {
                        Registration = matricula,
                        StoreId = loja.Id,
                        Contracts =
                        [
                            new ContractVersion
                            {
                                // Padrão vigente: jornada controlada, 1 domingo a cada 3; carga semanal = regra carga_horaria_semanal
                                ValidFrom = c.DataAdmissao is { } adm && adm <= hoje ? adm : hoje,
                                JourneyControlled = true,
                                SundayRule = SundayRule.OneInThreeWeeks,
                                CreatedBy = Autor
                            }
                        ]
                    };
                    db.Employees.Add(e);
                    porLoja[(loja.Id, matricula)] = e;
                    criados++;
                }
                vistos.Add(e.Id);

                var mudou = Aplicar(e, c, empresa, agora);
                if (e.LgInativadoEm is not null)
                {
                    if (!e.Active)
                    {
                        e.Active = true;
                        reativados++;
                        EventoStatus(e, null, "reativado", $"voltou na LG em {hoje:dd/MM/yyyy}");
                    }
                    e.LgInativadoEm = null;
                    mudou = true;
                }

                // Setor padrão = setor do órgão responsável (não sobrescreve setores alterados manualmente)
                Guid? setorLg = c.OrgaoCodigo is { } orgao && setoresLg.TryGetValue(loja.Id, out var mapa) && mapa.TryGetValue(orgao, out var sid)
                    ? sid : null;
                var (mudouSetor, removidos) = LgRegras.AplicarSetorLg(e, c.OrgaoCodigo, c.OrgaoResponsavel?.Descricao, setorLg);
                if (removidos.Count > 0) db.RemoveRange(removidos);
                mudou |= mudouSetor;

                if (!novo)
                {
                    if (mudou) atualizados++;
                    else inalterados++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                nErros++;
                Adicionar(erros, $"Matrícula {matricula}: {ex.Message}");
            }
        }

        // Quem veio da LG antes (empresas do filtro) e não veio agora: inativa (não apaga)
        if (consulta.Contratos.Count == 0)
        {
            Adicionar(avisos, "A LG não devolveu nenhum colaborador: ninguém foi inativado (confira os filtros LG:Empresas e LG:TiposDeSituacoes).");
        }
        else
        {
            var empresas = o.EmpresasEfetivas.ToHashSet();
            foreach (var e in todos.Where(x => x.Active && x.LgEmpresaCodigo is { } emp && empresas.Contains(emp) && !vistos.Contains(x.Id)))
            {
                var duplicado = chavesLg.Contains((e.LgEmpresaCodigo!.Value, e.Registration));
                Inativar(e, duplicado
                    ? "cadastro repetido: pela LG a matrícula está em outra loja"
                    : $"não veio na LG em {hoje:dd/MM/yyyy} (demitido, transferido ou fora dos filtros)");
            }
        }

        await db.SaveChangesAsync(ct);

        // Férias de todos os ativos (antes das contagens: o período da LG resolve a pendência de período)
        var resumoFerias = await FeriasAsync(o, por, progresso, avisos, ct);

        var semSetor = todos.Concat(db.ChangeTracker.Entries<Domain.Employee>().Select(x => x.Entity)).DistinctBy(x => x.Id)
            .Where(x => x.Active && x.LgEmpresaCodigo is not null && x.Sectors.Count == 0).ToList();
        var pendencias = todos.Concat(db.ChangeTracker.Entries<Domain.Employee>().Select(x => x.Entity)).DistinctBy(x => x.Id)
            .Count(LgRegras.PendenciaDePeriodo);
        var divergentes = todos.Concat(db.ChangeTracker.Entries<Domain.Employee>().Select(x => x.Entity)).DistinctBy(x => x.Id)
            .Count(x => x.Active && x.LgEmpresaCodigo is not null && LgRegras.SetorDivergente(x));
        if (semSetor.Count > 0)
            Adicionar(avisos, $"{semSetor.Count} colaborador(es) sem setor (órgão responsável vazio na LG): não entram na geração até o gestor definir o setor.");
        if (divergentes > 0)
            Adicionar(avisos, $"{divergentes} colaborador(es) com setor alterado manualmente diferente do órgão responsável da LG (mantido o manual; veja \"Setor da LG\" na edição).");
        if (pendencias > 0)
            Adicionar(avisos, $"{pendencias} colaborador(es) em férias/afastamento na LG sem período definido: ficam fora da escala até o período ser registrado.");

        var centros = ignorados.Values
            .Select(v => new LgCentroIgnorado(v.Centro?.Codigo, v.Centro?.Descricao, v.Matriculas.Count, v.Matriculas.Take(20).ToList()))
            .OrderByDescending(v => v.Quantidade).ToList();
        var resumo = new LgResumo("Concluida", hoje, inicio, DateTime.UtcNow, por, consulta.Paginas, consulta.Contratos.Count,
            criados, atualizados, inalterados, nIgnorados, inativados, movidos, reativados, semSetor.Count, pendencias, nErros,
            centros, avisos, erros, null,
            resumoSetores.Sum(s => s.Criados), resumoSetores.Sum(s => s.Adotados), divergentes, resumoSetores, resumoFerias);
        logger.LogInformation(
            "LG: sincronização concluída por {Por}: {Total} registro(s), {Criados} criado(s), {Atualizados} atualizado(s), {Ignorados} ignorado(s), {Inativados} inativado(s), {Movidos} movido(s), {Erros} erro(s), setores {SetoresCriados} criado(s)/{SetoresAdotados} adotado(s)",
            por, resumo.TotalLg, criados, atualizados, nIgnorados, inativados, movidos, nErros, resumo.SetoresCriados, resumo.SetoresAdotados);

        foreach (var ev in eventos) await PublicarAsync(ev, ct);
        await PublicarAsync(Evento(resumo), ct);
        return resumo;
    }

    /// <summary>
    /// Férias dos ativos depois da gravação dos colaboradores. Desligada (LG:Ferias:AposSincronizarColaboradores) ou sem
    /// configuração: null/aviso. Falha (exceto cancelamento): aviso, a sincronização dos colaboradores continua concluída.
    /// </summary>
    private async Task<LgFeriasResumo?> FeriasAsync(LgOptions o, string por, Action<string, int, int>? progresso, List<string> avisos, CancellationToken ct)
    {
        if (ferias is null || !o.Ferias.AposSincronizarColaboradores) return null;
        if (!o.ConfiguradaPara(LgFeriasClient.Operacao(o)))
        {
            Adicionar(avisos, $"Férias não consultadas: {o.MensagemNaoConfigurada(LgFeriasClient.Operacao(o))}");
            return null;
        }
        try
        {
            var r = await ferias.SincronizarAsync(new LgFeriasFiltro(), por, progresso, ct);
            if (r.Falhas > 0)
                Adicionar(avisos, $"Férias: {r.Falhas} colaborador(es) não consultado(s) na LG (mantidos os períodos já gravados)" +
                                  $"{(r.Mensagem is { Length: > 0 } m ? $": {m}" : ".")}");
            return r;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("LG: férias não consultadas depois dos colaboradores ({Motivo})", $"{ex.GetType().Name}: {ex.Message}");
            Adicionar(avisos, $"Férias não atualizadas ({ex.Message}); mantidos os períodos já gravados. Use \"Atualizar férias\" ou aguarde a próxima sincronização.");
            return null;
        }
    }

    /// <summary>Evento de auditoria do resumo (sem dados pessoais: só contagens e centros de custo).</summary>
    public static EmployeesLgSyncCompleted Evento(LgResumo r) =>
        new(r.Status, r.DataVigente, r.Paginas, r.TotalLg, r.Criados, r.Atualizados, r.Inalterados, r.Ignorados, r.Inativados,
            r.Movidos, r.Reativados, r.SemSetor, r.PendenciasDePeriodo, r.Erros,
            r.CentrosIgnorados.Select(c => $"{c.Codigo} {c.Descricao} ({c.Quantidade})").ToList(), r.Mensagem, r.Por, r.IniciadaEm, r.ConcluidaEm,
            r.SetoresCriados, r.SetoresAdotados, r.SetoresDivergentes, r.Ferias?.Consultados ?? 0, r.Ferias?.Falhas ?? 0);

    /// <summary>
    /// Órgãos responsáveis por loja (mesma regra de loja do upsert; registros sem matrícula ou sem loja ficam de fora).
    /// Órgão sem código numérico é ignorado (o colaborador fica sem setor).
    /// </summary>
    public static Dictionary<Guid, List<LgOrgaoSetor>> OrgaosPorLoja(IEnumerable<LgContrato> contratos, IReadOnlyList<LojaEscala> lojas,
        IReadOnlyDictionary<string, string>? mapa)
    {
        var r = new Dictionary<Guid, List<LgOrgaoSetor>>();
        foreach (var c in contratos)
        {
            if (c.OrgaoCodigo is not { } codigo || c.Matricula.Trim().Length is 0 or > 30) continue;
            if (LgRegras.LojaDoCentroDeCusto(c.CentroDeCusto, lojas, mapa).Loja is not { } loja) continue;
            if (!r.TryGetValue(loja.Id, out var lista)) r[loja.Id] = lista = [];
            if (lista.All(x => x.Codigo != codigo)) lista.Add(new LgOrgaoSetor(codigo, c.OrgaoResponsavel?.Descricao, c.OrgaoStatus));
        }
        return r;
    }

    /// <summary>Publica sem derrubar a sincronização (RabbitMQ fora do ar: só aviso no log).</summary>
    public async Task PublicarAsync<T>(T evento, CancellationToken ct) where T : class
    {
        try
        {
            using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limite.CancelAfter(TimeSpan.FromSeconds(15));
            await bus.Publish(evento, limite.Token);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("LG: evento {Tipo} não publicado ({Motivo})", typeof(T).Name, ex.Message);
        }
    }

    /// <summary>Aplica os dados da LG no colaborador; true = algo mudou (além da data da sincronização).</summary>
    public static bool Aplicar(Domain.Employee e, LgContrato c, int empresa, DateTime agora)
    {
        var nome = LgRegras.NomeProprio(c.Nome);
        if (nome.Length == 0) nome = e.Name.Length > 0 ? e.Name : c.Matricula;
        if (nome.Length > 200) nome = nome[..200];
        var cargo = c.Cargo?.Descricao?.Trim() ?? e.JobTitle;
        if (cargo.Length > 100) cargo = cargo[..100];
        var situacaoDescricao = c.Situacao?.Descricao is { } sd && sd.Length > 200 ? sd[..200] : c.Situacao?.Descricao;
        var centro = c.CentroDeCusto?.Codigo is { } cc && cc.Length > 30 ? cc[..30] : c.CentroDeCusto?.Codigo;
        var json = c.Json.ToJsonString();

        var mudou = e.Name != nome || e.JobTitle != cargo || e.LgEmpresaCodigo != empresa || e.LgSituacaoCodigo != c.SituacaoCodigo
                    || e.LgSituacaoDescricao != situacaoDescricao || e.LgDataInicioSituacao != c.DataInicioSituacaoAtual
                    || e.LgDataAdmissao != c.DataAdmissao || e.LgCentroDeCusto != centro || !MesmoJson(e.LgRegistroJson, c.Json);
        e.Name = nome;
        e.JobTitle = cargo;
        e.LgEmpresaCodigo = empresa;
        e.LgSituacaoCodigo = c.SituacaoCodigo;
        e.LgSituacaoDescricao = situacaoDescricao;
        e.LgDataInicioSituacao = c.DataInicioSituacaoAtual;
        e.LgDataAdmissao = c.DataAdmissao;
        e.LgCentroDeCusto = centro;
        e.LgRegistroJson = json;
        e.LgSincronizadoEm = agora;
        return mudou;
    }

    /// <summary>O jsonb do PostgreSQL reordena as chaves e muda os espaços: compara pelo conteúdo, não pelo texto.</summary>
    private static bool MesmoJson(string? gravado, System.Text.Json.Nodes.JsonObject novo)
    {
        if (string.IsNullOrEmpty(gravado)) return false;
        try
        {
            return System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(gravado), novo);
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    private static void Adicionar(List<string> lista, string texto)
    {
        if (lista.Count < MaxMensagens) lista.Add(texto);
        else if (lista.Count == MaxMensagens) lista.Add("... (lista truncada)");
    }
}

namespace Escala5x2.BuildingBlocks.Shared.Lg;

/// <summary>
/// Operação SOAP da LG: nome do elemento da operação (o resultado é "{Nome}Result"), a chave em LG:Operacoes e os
/// namespaces. Os serviços WCF da LG não usam todos a mesma versão: colaboradores e centros de custo têm a operação em
/// "lg.com.br/api/v2"; órgãos responsáveis, em "lg.com.br/api/v1". O filtro (DTO) é "lg.com.br/api/dto/v1" em todas.
/// </summary>
/// <param name="UrlPropria">
/// true = o endpoint da operação é obrigatório em LG:Operacoes:{Chave}:Url (não cai no LG:Url, que é o serviço de
/// colaboradores e responderia "sem {Nome}Result" para cada consulta).
/// </param>
public sealed record LgOperacao(string Nome, string Chave, string NamespaceOperacao = LgOperacao.ApiV2, string NamespaceDto = LgOperacao.DtoV1,
    bool UrlPropria = false)
{
    public const string ApiV1 = "lg.com.br/api/v1";
    public const string ApiV2 = "lg.com.br/api/v2";
    public const string DtoV1 = "lg.com.br/api/dto/v1";

    /// <summary>Colaboradores (Employee.Api): ConsultarListaHistoricosVigenteNaDataPorDemanda.</summary>
    public static readonly LgOperacao Colaboradores = new("ConsultarListaHistoricosVigenteNaDataPorDemanda", "Colaboradores");

    /// <summary>Centros de custo = lojas (Store.Api): ConsultarListaCentroDeCustoCompletoPorDemanda.</summary>
    public static readonly LgOperacao CentrosDeCusto = new("ConsultarListaCentroDeCustoCompletoPorDemanda", "CentrosDeCusto");

    /// <summary>
    /// Órgãos responsáveis = catálogo de setores da empresa (Store.Api): ConsultarListaPorDemanda, em
    /// "lg.com.br/api/v1" (outro serviço WCF, em geral com .svc próprio: LG:Operacoes:OrgaosResponsaveis:Url).
    /// </summary>
    public static readonly LgOperacao OrgaosResponsaveis = new("ConsultarListaPorDemanda", "OrgaosResponsaveis", ApiV1);

    /// <summary>
    /// Férias de um contrato (Employee.Api): ConsultarLista do ServicoDeFerias v2 (WSDL público:
    /// https://prd-api1.lg.com.br/v2/servicodeferias?wsdl; SOAPAction "lg.com.br/api/v2/ServicoDeFerias/ConsultarLista").
    /// Filtro FiltroComIdentificacaoDeContrato (Colaborador/Empresa/Codigo + Matricula) no namespace do DTO
    /// (lg.com.br/api/dto/v1, pelo WSDL; LG:Ferias:NamespaceFiltro troca se a LG pedir outro). Resposta sem paginação.
    /// Outro serviço WCF: LG:Operacoes:Ferias:Url é obrigatório.
    /// </summary>
    public static readonly LgOperacao Ferias = new("ConsultarLista", "Ferias", UrlPropria: true);
}

/// <summary>Endereço e SOAPAction de uma operação (LG:Operacoes:{Chave}). Vazio = usa o geral (LG:Url / LG:SoapAction).</summary>
public sealed class LgOperacaoOptions
{
    /// <summary>Endpoint do .svc da operação (cada serviço WCF da LG pode ter o seu). Vazio = LG:Url.</summary>
    public string Url { get; set; } = string.Empty;
    /// <summary>Cabeçalho SOAPAction da operação (SOAP 1.1).</summary>
    public string SoapAction { get; set; } = string.Empty;
}

public sealed class LgOperacoesOptions
{
    /// <summary>ConsultarListaHistoricosVigenteNaDataPorDemanda. SoapAction vazio = LG:SoapAction (compatibilidade).</summary>
    public LgOperacaoOptions Colaboradores { get; set; } = new();
    /// <summary>ConsultarListaCentroDeCustoCompletoPorDemanda. SoapAction obrigatório para sincronizar as lojas.</summary>
    public LgOperacaoOptions CentrosDeCusto { get; set; } = new();
    /// <summary>
    /// ConsultarListaPorDemanda (OrgaoResponsavel, namespace lg.com.br/api/v1). SoapAction obrigatório para sincronizar o
    /// catálogo de setores; Url vazio = LG:Url (provavelmente é outro serviço WCF: informe o .svc dos órgãos).
    /// </summary>
    public LgOperacaoOptions OrgaosResponsaveis { get; set; } = new();
    /// <summary>
    /// ConsultarLista do ServicoDeFerias v2 (Employee.Api). Url E SoapAction obrigatórios (sem eles as férias não são
    /// consultadas e a sincronização de colaboradores só avisa). Valores do WSDL público da LG:
    /// Url https://prd-api1.lg.com.br/v2/servicodeferias e SoapAction lg.com.br/api/v2/ServicoDeFerias/ConsultarLista.
    /// </summary>
    public LgOperacaoOptions Ferias { get; set; } = new();
}

/// <summary>Seção LG:Ferias: períodos de férias por colaborador (Employee.Api), gravados como ausências "Férias" de origem LG.</summary>
public sealed class LgFeriasOptions
{
    /// <summary>Consultas simultâneas à LG (uma por matrícula). Limitado a 1..16.</summary>
    public int Paralelismo { get; set; } = 6;
    /// <summary>Períodos que terminaram há mais que isto (meses) não são importados (histórico já fora das escalas).</summary>
    public int MesesPassados { get; set; } = 12;
    /// <summary>
    /// Antes de gerar uma escala, férias consultadas há menos que isto (minutos) não são consultadas de novo (repetir a
    /// geração não refaz as chamadas). 0 = sempre consulta.
    /// </summary>
    public int ValidadeMinutos { get; set; } = 10;
    /// <summary>
    /// Falhas seguidas que interrompem a rodada (LG fora do ar ou configuração errada: evita milhares de chamadas que vão
    /// falhar). As matrículas não consultadas contam como falha e mantêm os períodos já gravados.
    /// </summary>
    public int FalhasSeguidasParaInterromper { get; set; } = 20;
    /// <summary>A sincronização de colaboradores (botão e diária) consulta as férias de todos os ativos logo depois.</summary>
    public bool AposSincronizarColaboradores { get; set; } = true;
    /// <summary>Namespace dos elementos do filtro (vazio = lg.com.br/api/dto/v1, o do WSDL).</summary>
    public string NamespaceFiltro { get; set; } = string.Empty;

    public int ParalelismoEfetivo => Math.Clamp(Paralelismo, 1, 16);
}

/// <summary>Seção LG:Lojas: lojas a partir dos centros de custo (Store.Api).</summary>
public sealed class LgLojasOptions
{
    /// <summary>Padrão usado quando LG:Lojas:Padrao está vazio.</summary>
    public const string PadraoPadrao = @"^(VAREJO|ATACAREJO|ATACADO)\b.*\bLOJA\s*(?<numero>\d{1,3})";

    /// <summary>
    /// Expressão regular aplicada à descrição do centro de custo (sem acento, em maiúsculas; sem diferenciar maiúsculas).
    /// Precisa do grupo "numero". Casou = é loja; Code = número com 2 dígitos ("LOJA 1" → "01").
    /// </summary>
    public string Padrao { get; set; } = PadraoPadrao;
    /// <summary>
    /// Mapeamento manual CentroDeCusto.Codigo → Code da loja (ex.: { "301005": "35" }). Tem precedência sobre o padrão,
    /// resolve conflito de número (varejo e atacarejo "LOJA 05") e inclui centro de custo fora do padrão (ex.: CD).
    /// </summary>
    public Dictionary<string, string> Mapeamento { get; set; } = [];
    /// <summary>Hora da sincronização diária das lojas ("HH:mm", fuso LG:FusoHorario). Vazio = desligada (padrão).</summary>
    public string SincronizacaoDiaria { get; set; } = string.Empty;
    /// <summary>
    /// Employee.Api: o botão/rotina de colaboradores sincroniza as lojas antes (chamada interna ao Store.Api com
    /// X-Internal-Key). Desligado por padrão.
    /// </summary>
    public bool SincronizarAntesDosColaboradores { get; set; }

    public string PadraoEfetivo => string.IsNullOrWhiteSpace(Padrao) ? PadraoPadrao : Padrao;
}

/// <summary>
/// Seção "LG" da configuração: integração SOAP com a LG (lg.com.br), usada pelo Employee.Api (colaboradores) e pelo
/// Store.Api (lojas pelos centros de custo e setores pelos órgãos responsáveis).
/// CREDENCIAIS NUNCA NO REPOSITÓRIO: o appsettings só tem placeholders vazios; os valores vêm de variáveis de ambiente
/// (LG__Usuario, LG__Senha, LG__GuidTenant, LG__Url, LG__Operacoes__...__SoapAction), user-secrets (Development) ou
/// Docker secrets (/run/secrets/LG__Senha). Ver README do Escala5x2.
/// </summary>
public sealed class LgOptions
{
    public const string Section = "LG";

    /// <summary>Endereço do serviço SOAP (endpoint do .svc). Vazio = integração não configurada.</summary>
    public string Url { get; set; } = string.Empty;
    /// <summary>
    /// SOAPAction da operação de colaboradores (compatibilidade; o mesmo que LG:Operacoes:Colaboradores:SoapAction,
    /// que tem precedência).
    /// </summary>
    public string SoapAction { get; set; } = string.Empty;
    /// <summary>Endereço e SOAPAction por operação.</summary>
    public LgOperacoesOptions Operacoes { get; set; } = new();
    public string Usuario { get; set; } = string.Empty;
    public string Senha { get; set; } = string.Empty;
    public string GuidTenant { get; set; } = string.Empty;
    /// <summary>LGContextoAmbiente/Ambiente.</summary>
    public int Ambiente { get; set; } = 705;
    /// <summary>
    /// Filtro de empresas (colaboradores: Empresas; centros de custo e órgãos responsáveis: uma chamada por empresa; órgão
    /// vira setor só se habilitado para uma delas). Vazio = [1].
    /// </summary>
    public List<int> Empresas { get; set; } = [];
    /// <summary>Filtro TiposDeSituacoes (arr:int). Vazio na configuração = [2, 3].</summary>
    public List<int> TiposDeSituacoes { get; set; } = [];
    /// <summary>Tempo máximo de cada chamada (segundos).</summary>
    public int TimeoutSegundos { get; set; } = 60;
    /// <summary>
    /// Tamanho da página pedido à LG (elemento v1:QuantidadePorPagina do filtro de colaboradores). 0 = não envia (a LG
    /// usa o padrão dela, 50). Só preencha se a LG aceitar o elemento no filtro.
    /// </summary>
    public int TamanhoPagina { get; set; }
    /// <summary>Tentativas por página em erro transitório (rede, timeout, HTTP 408/429/5xx sem SOAP Fault).</summary>
    public int Tentativas { get; set; } = 3;
    /// <summary>Espera inicial entre tentativas (dobra a cada tentativa).</summary>
    public int EsperaInicialMs { get; set; } = 1000;
    /// <summary>Limite de segurança de páginas por consulta.</summary>
    public int MaxPaginas { get; set; } = 1000;
    /// <summary>Fuso para a DataVigente (hoje), a vigência dos centros de custo e as sincronizações diárias.</summary>
    public string FusoHorario { get; set; } = "America/Recife";
    /// <summary>
    /// Hora da sincronização diária automática dos colaboradores ("HH:mm", fuso <see cref="FusoHorario"/>). Vazio = desligada (padrão).
    /// </summary>
    public string SincronizacaoDiaria { get; set; } = string.Empty;
    /// <summary>
    /// Colaboradores: centros de custo sem "LOJA nn" na descrição (CD, administrativo...): CentroDeCusto.Codigo →
    /// Code da loja no Store.Api (ex.: { "301000": "21" }). Usado depois do vínculo pelo código LG da loja.
    /// </summary>
    public Dictionary<string, string> CentrosDeCusto { get; set; } = [];
    /// <summary>Lojas a partir dos centros de custo (Store.Api).</summary>
    public LgLojasOptions Lojas { get; set; } = new();
    /// <summary>Férias por colaborador (Employee.Api).</summary>
    public LgFeriasOptions Ferias { get; set; } = new();

    public IReadOnlyList<int> EmpresasEfetivas => Empresas.Count > 0 ? Empresas : [1];
    public IReadOnlyList<int> SituacoesEfetivas => TiposDeSituacoes.Count > 0 ? TiposDeSituacoes : [2, 3];

    private LgOperacaoOptions? Da(LgOperacao op) =>
        op.Chave == LgOperacao.Colaboradores.Chave ? Operacoes.Colaboradores
        : op.Chave == LgOperacao.CentrosDeCusto.Chave ? Operacoes.CentrosDeCusto
        : op.Chave == LgOperacao.OrgaosResponsaveis.Chave ? Operacoes.OrgaosResponsaveis
        : op.Chave == LgOperacao.Ferias.Chave ? Operacoes.Ferias
        : null;

    /// <summary>Endpoint da operação: LG:Operacoes:{op}:Url ou, vazio, LG:Url (exceto operação com URL própria).</summary>
    public string UrlDe(LgOperacao op) =>
        Da(op)?.Url is { Length: > 0 } u && !string.IsNullOrWhiteSpace(u) ? u.Trim()
        : op.UrlPropria ? string.Empty
        : Url.Trim();

    /// <summary>SOAPAction da operação: LG:Operacoes:{op}:SoapAction; colaboradores aceitam também LG:SoapAction.</summary>
    public string SoapActionDe(LgOperacao op)
    {
        if (Da(op)?.SoapAction is { } s && !string.IsNullOrWhiteSpace(s)) return s.Trim();
        return op.Chave == LgOperacao.Colaboradores.Chave ? SoapAction.Trim() : string.Empty;
    }

    /// <summary>Chaves obrigatórias que faltam para a operação (nomes, nunca valores). Padrão: colaboradores.</summary>
    public IReadOnlyList<string> Faltando(LgOperacao? op = null)
    {
        op ??= LgOperacao.Colaboradores;
        var colaboradores = op.Chave == LgOperacao.Colaboradores.Chave;
        var faltando = new List<string>();
        if (string.IsNullOrWhiteSpace(UrlDe(op)))
            faltando.Add(colaboradores ? "LG:Url" : op.UrlPropria ? $"LG:Operacoes:{op.Chave}:Url" : $"LG:Url (ou LG:Operacoes:{op.Chave}:Url)");
        if (string.IsNullOrWhiteSpace(SoapActionDe(op)))
            faltando.Add(colaboradores ? "LG:SoapAction (ou LG:Operacoes:Colaboradores:SoapAction)" : $"LG:Operacoes:{op.Chave}:SoapAction");
        if (string.IsNullOrWhiteSpace(Usuario)) faltando.Add("LG:Usuario");
        if (string.IsNullOrWhiteSpace(Senha)) faltando.Add("LG:Senha");
        if (string.IsNullOrWhiteSpace(GuidTenant)) faltando.Add("LG:GuidTenant");
        return faltando;
    }

    /// <summary>Configurada para os colaboradores (compatibilidade).</summary>
    public bool Configurada => ConfiguradaPara(LgOperacao.Colaboradores);

    public bool ConfiguradaPara(LgOperacao op) => Faltando(op).Count == 0;

    /// <summary>Mensagem padrão quando falta configuração (cita só as chaves).</summary>
    public string MensagemNaoConfigurada(LgOperacao? op = null) =>
        $"Integração LG não configurada: informe {string.Join(", ", Faltando(op))} (variáveis de ambiente LG__..., user-secrets ou secrets do contêiner).";

    /// <summary>Hora diária ("HH:mm"); null = desligada ou inválida. Sem argumento: a dos colaboradores.</summary>
    public TimeOnly? HoraDiaria(string? valor = null) => Hora(valor ?? SincronizacaoDiaria);

    public static TimeOnly? Hora(string? valor) =>
        TimeOnly.TryParseExact(valor?.Trim(), ["HH:mm", "H:mm"], null, System.Globalization.DateTimeStyles.None, out var h) ? h : null;

    public TimeZoneInfo Fuso()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(string.IsNullOrWhiteSpace(FusoHorario) ? "America/Recife" : FusoHorario);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            // Recife: UTC-3 sem horário de verão
            return TimeZoneInfo.CreateCustomTimeZone("America/Recife", TimeSpan.FromHours(-3), "America/Recife", "America/Recife");
        }
    }

    /// <summary>Hoje no fuso da LG (DataVigente).</summary>
    public DateOnly Hoje(DateTime? utcAgora = null) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utcAgora ?? DateTime.UtcNow, Fuso()));
}

using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using Escala5x2.BuildingBlocks.Contracts.Stores;
using Escala5x2.BuildingBlocks.Shared.Auth;
using Escala5x2.BuildingBlocks.Shared.Demo;
using Escala5x2.BuildingBlocks.Shared.Lg;
using Store.Api.Domain;

namespace Store.Api.Lg;

/// <summary>Resultado da aplicação dos centros de custo nas lojas (em memória; quem chama grava e publica).</summary>
public sealed class LgLojasResultado
{
    public List<Domain.Store> Novas { get; } = [];
    public List<StoreLgChanged> Eventos { get; } = [];
    public List<LgLojaAlterada> Alteracoes { get; } = [];
    public List<LgCentroNaoLoja> Ignorados { get; } = [];
    public List<LgConflitoLoja> Conflitos { get; } = [];
    public List<string> Avisos { get; } = [];
    public int Lojas { get; set; }
    public int Criadas { get; set; }
    public int Atualizadas { get; set; }
    public int Inalteradas { get; set; }
    public int Inativadas { get; set; }
    public int Reativadas { get; set; }
}

/// <summary>
/// Regras da sincronização de lojas pelos centros de custo da LG:
/// <list type="bullet">
/// <item>É loja o centro de custo mapeado em LG:Lojas:Mapeamento (código → Code; valor vazio = não é loja) ou cuja
/// descrição (sem acento, maiúsculas) casa com LG:Lojas:Padrao — padrão <c>^(VAREJO|ATACAREJO|ATACADO)\b.*\bLOJA\s*(?&lt;numero&gt;\d{1,3})</c>.
/// Os demais (PRESIDENCIA, RESIDÊNCIA, INDUSTRIA...) são ignorados e listados.</item>
/// <item>Code = número com 2 dígitos ("VAREJO LOJA 01 - CABO" → "01", o mesmo do perfil da intranet "01 - CABO");
/// Name = Descricao completa; Format pela primeira palavra (VAREJO/ATACAREJO/ATACADO); LgCentroDeCustoCodigo = Codigo.</item>
/// <item>Ativa = Status 0 e DataFim vazia ou não passada. Encerrado → loja inativada (nunca apagada); centro de custo
/// encerrado sem loja na Escala não cria loja.</item>
/// <item>Upsert: pelo LgCentroDeCustoCodigo; senão pelo código normalizado (adota a loja da lista da intranet/cadastrada à
/// mão). Só nome, formato e ativo vêm da LG: cidade, UF, setores, turnos, demanda e operação editados ficam.</item>
/// <item>Conflito: dois centros de custo com o mesmo código de loja (ex.: varejo e atacarejo "LOJA 05") não criam
/// duplicado; ficam no status até o mapeamento manual. Se só um deles já está vinculado à loja, ele continua.</item>
/// <item>A loja DEMO nunca é tocada.</item>
/// </list>
/// </summary>
public static class LgLojasRegras
{
    private const int MaxLista = 300;
    private static readonly ConcurrentDictionary<string, Regex> Padroes = new();

    /// <summary>Regex do padrão (cache). Inválido → <see cref="LgIntegracaoException"/> citando LG:Lojas:Padrao.</summary>
    public static Regex Padrao(string padrao)
    {
        try
        {
            return Padroes.GetOrAdd(padrao, p =>
                new Regex(p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)));
        }
        catch (ArgumentException ex)
        {
            throw new LgIntegracaoException($"LG:Lojas:Padrao inválido ({ex.Message}).", inner: ex);
        }
    }

    /// <summary>Número da loja na descrição (grupo "numero" do padrão; sem o grupo, o último grupo); null = não é loja.</summary>
    public static string? NumeroDaLoja(string? descricao, Regex padrao)
    {
        var m = padrao.Match(LgXml.Normalizar(descricao));
        if (!m.Success) return null;
        var g = m.Groups["numero"];
        if (!g.Success && m.Groups.Count > 1) g = m.Groups[^1];
        return g.Success && g.Value.Trim().Length > 0 ? g.Value.Trim() : null;
    }

    /// <summary>"1" → "01", "27" → "27", "123" → "123" (mesmo formato da lista da intranet).</summary>
    public static string CodigoDaLoja(string numero) =>
        int.TryParse(numero, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n.ToString("00", CultureInfo.InvariantCulture) : numero.Trim().ToUpperInvariant();

    /// <summary>Formato pela primeira palavra da descrição: VAREJO, ATACAREJO ou ATACADO; outro → null (mantém o atual).</summary>
    public static StoreFormat? Formato(string? descricao) =>
        LgXml.Normalizar(descricao).Split(' ', 2)[0] switch
        {
            "VAREJO" => StoreFormat.Varejo,
            "ATACAREJO" => StoreFormat.Atacarejo,
            "ATACADO" => StoreFormat.Atacado,
            _ => null
        };

    /// <summary>Status 0 (ou vazio) e DataFim vazia ou de hoje em diante.</summary>
    public static bool Ativo(LgCentroDeCusto c, DateOnly hoje) => (c.Status is null or 0) && (c.DataFim is null || c.DataFim >= hoje);

    private static string Motivo(LgCentroDeCusto c, DateOnly _) =>
        c.Status is not (null or 0) ? $"Status {c.Status} na LG" : $"DataFim {c.DataFim:dd/MM/yyyy}";

    private static string Rotulo(LgCentroDeCusto c) => $"{c.Codigo} {c.Descricao}".Trim();

    private static string Descrever(Domain.Store s) => $"{s.Code} - {s.Name}, {s.Format}, {(s.Active ? "ativa" : "inativa")}";

    private static string NomeDe(LgCentroDeCusto c, Domain.Store? atual)
    {
        var nome = c.Descricao?.Trim() ?? string.Empty;
        if (nome.Length == 0) nome = atual?.Name ?? c.Codigo;
        return nome.Length > 200 ? nome[..200] : nome;
    }

    private static void Adicionar<T>(List<T> lista, T item)
    {
        if (lista.Count < MaxLista) lista.Add(item);
    }

    private sealed record Candidato(LgCentroDeCusto Centro, string Code, bool Mapeado, bool Ativo, Domain.Store? Vinculada);

    /// <summary>
    /// Aplica os centros de custo em <paramref name="lojas"/> (todas as lojas cadastradas; as novas são acrescentadas
    /// nela e em <see cref="LgLojasResultado.Novas"/>). Puro: não grava nem publica.
    /// </summary>
    public static LgLojasResultado Aplicar(IReadOnlyList<LgCentroDeCusto> centros, List<Domain.Store> lojas, LgLojasOptions o,
        DateOnly hoje, string por, DateTime agora)
    {
        var r = new LgLojasResultado();
        var padrao = Padrao(o.PadraoEfetivo);
        var mapa = o.Mapeamento
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Key))
            .GroupBy(kv => kv.Key.Trim(), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Last().Value?.Trim() ?? string.Empty, StringComparer.Ordinal);
        var vistos = new HashSet<string>(StringComparer.Ordinal);

        // 1) Quais centros de custo são lojas
        var candidatos = new List<Candidato>();
        foreach (var c in centros)
        {
            if (c.Codigo.Length == 0)
            {
                Adicionar(r.Ignorados, new LgCentroNaoLoja(c.Codigo, c.Descricao, "sem código na LG"));
                continue;
            }
            if (!vistos.Add(c.Codigo)) continue;

            string code;
            var mapeado = mapa.TryGetValue(c.Codigo, out var codigoMapeado);
            if (mapeado)
            {
                if (codigoMapeado!.Length == 0)
                {
                    Adicionar(r.Ignorados, new LgCentroNaoLoja(c.Codigo, c.Descricao, "LG:Lojas:Mapeamento vazio (não é loja)"));
                    continue;
                }
                code = codigoMapeado.ToUpperInvariant();
            }
            else if (NumeroDaLoja(c.Descricao, padrao) is { } numero)
            {
                code = CodigoDaLoja(numero);
            }
            else
            {
                Adicionar(r.Ignorados, new LgCentroNaoLoja(c.Codigo, c.Descricao, "fora do padrão de loja (LG:Lojas:Padrao)"));
                continue;
            }

            if (code.Length > 20)
            {
                Adicionar(r.Ignorados, new LgCentroNaoLoja(c.Codigo, c.Descricao, $"código de loja \"{code}\" com mais de 20 caracteres"));
                continue;
            }
            if (CodigoLoja.Normalizar(code) == "DEMO")
            {
                Adicionar(r.Ignorados, new LgCentroNaoLoja(c.Codigo, c.Descricao, "código DEMO é reservado à loja de demonstração"));
                continue;
            }

            var ativo = Ativo(c, hoje);
            var vinculada = lojas.FirstOrDefault(l => string.Equals(l.LgCentroDeCustoCodigo, c.Codigo, StringComparison.Ordinal));
            if (!ativo && vinculada is null)
            {
                Adicionar(r.Ignorados, new LgCentroNaoLoja(c.Codigo, c.Descricao, $"encerrado na LG ({Motivo(c, hoje)}) e sem loja na Escala"));
                continue;
            }
            candidatos.Add(new Candidato(c, code, mapeado, ativo, vinculada));
        }

        // 2) Conflitos: mesmo código de loja para mais de um centro de custo
        var processar = new List<Candidato>();
        foreach (var g in candidatos.GroupBy(x => CodigoLoja.Normalizar(x.Code) ?? x.Code))
        {
            var lista = g.ToList();
            if (lista.Count == 1)
            {
                processar.Add(lista[0]);
                continue;
            }
            // Só um deles já é a loja deste código: ele continua; os outros esperam o mapeamento
            var donos = lista.Where(x => x.Vinculada is { } v && CodigoLoja.Normalizar(v.Code) == g.Key).ToList();
            if (donos.Count == 1) processar.Add(donos[0]);
            var emConflito = lista.Except(donos.Count == 1 ? donos : new List<Candidato>()).ToList();
            var codigos = emConflito.Select(x => Rotulo(x.Centro)).ToList();
            var exemplo = emConflito.Last().Centro.Codigo;
            Adicionar(r.Conflitos, new LgConflitoLoja(lista[0].Code, codigos,
                (donos.Count == 1
                    ? $"Centro(s) de custo {string.Join("; ", codigos)} também indicam a loja {lista[0].Code}, já vinculada ao centro de custo {donos[0].Centro.Codigo}. "
                    : $"Centros de custo {string.Join("; ", codigos)} indicam a mesma loja {lista[0].Code}: nenhuma loja foi criada ou alterada para eles. ")
                + $"Defina o código de cada um em LG:Lojas:Mapeamento (ex.: \"{exemplo}\": \"<código da loja>\")."));
        }

        // 3) Upsert
        foreach (var x in processar)
        {
            var c = x.Centro;
            var formato = Formato(c.Descricao);
            var loja = x.Vinculada;
            var adotada = false;
            if (loja is null)
            {
                var normalizado = CodigoLoja.Normalizar(x.Code);
                var porCodigo = lojas.FirstOrDefault(l => l.Id != DemoData.StoreId && CodigoLoja.Normalizar(l.Code) == normalizado);
                if (porCodigo is not null && !string.IsNullOrWhiteSpace(porCodigo.LgCentroDeCustoCodigo))
                {
                    Adicionar(r.Conflitos, new LgConflitoLoja(x.Code, [Rotulo(c)],
                        $"A loja {porCodigo.Code} já está vinculada ao centro de custo {porCodigo.LgCentroDeCustoCodigo}; o centro de custo {Rotulo(c)} " +
                        $"não foi aplicado. Defina o código dele em LG:Lojas:Mapeamento (ex.: \"{c.Codigo}\": \"<código da loja>\")."));
                    continue;
                }
                if (porCodigo is not null && porCodigo.Id == DemoData.StoreId) continue;
                loja = porCodigo;
                adotada = loja is not null;
            }

            r.Lojas++;
            if (loja is null)
            {
                loja = new Domain.Store
                {
                    Code = x.Code, Name = NomeDe(c, null), City = string.Empty, State = "PE", Format = formato ?? StoreFormat.Varejo,
                    Active = true, LgCentroDeCustoCodigo = c.Codigo, LgSincronizadoEm = agora
                };
                lojas.Add(loja);
                r.Novas.Add(loja);
                r.Criadas++;
                r.Eventos.Add(new StoreLgChanged(loja.Id, loja.Code, loja.Name, c.Codigo, "criada", null, por, agora));
                Adicionar(r.Alteracoes, new LgLojaAlterada(loja.Id, loja.Code, loja.Name, c.Codigo, "criada", $"{loja.Format}, sem cidade (informe em \"Editar loja\")"));
                continue;
            }

            var antes = Descrever(loja);
            var mudancas = new List<string>();
            if (adotada) mudancas.Add($"vinculada ao centro de custo {c.Codigo}");
            var nome = NomeDe(c, loja);
            if (loja.Name != nome) mudancas.Add($"nome \"{loja.Name}\" → \"{nome}\"");
            if (formato is { } f && loja.Format != f) mudancas.Add($"formato {loja.Format} → {f}");
            var estavaAtiva = loja.Active;
            loja.LgCentroDeCustoCodigo = c.Codigo;
            loja.Name = nome;
            if (formato is { } novo) loja.Format = novo;
            loja.Active = x.Ativo;
            loja.LgSincronizadoEm = agora;

            if (CodigoLoja.Normalizar(loja.Code) != CodigoLoja.Normalizar(x.Code))
                Adicionar(r.Avisos, $"O centro de custo {Rotulo(c)} indica a loja {x.Code}, mas está vinculado à loja {loja.Code}: o código da loja não muda " +
                                    "automaticamente (é o vínculo com o perfil da intranet). Ajuste LG:Lojas:Mapeamento se for o caso.");

            string? acao = null;
            if (estavaAtiva && !x.Ativo)
            {
                r.Inativadas++;
                acao = "inativada";
                mudancas.Add($"centro de custo encerrado ({Motivo(c, hoje)})");
            }
            else if (!estavaAtiva && x.Ativo)
            {
                r.Reativadas++;
                acao = "reativada";
            }
            else if (mudancas.Count > 0)
            {
                r.Atualizadas++;
                acao = "atualizada";
            }
            else
            {
                r.Inalteradas++;
            }
            if (acao is not null)
            {
                r.Eventos.Add(new StoreLgChanged(loja.Id, loja.Code, loja.Name, c.Codigo, acao, antes, por, agora));
                Adicionar(r.Alteracoes, new LgLojaAlterada(loja.Id, loja.Code, loja.Name, c.Codigo, acao, mudancas.Count > 0 ? string.Join("; ", mudancas) : null));
            }
        }

        // 4) Lojas vinculadas cujo centro de custo não veio (só aviso: nada é inativado por ausência)
        var codigosLg = centros.Select(c => c.Codigo).ToHashSet(StringComparer.Ordinal);
        foreach (var l in lojas.Where(l => l.LgCentroDeCustoCodigo is { } cc && !codigosLg.Contains(cc) && l.Active))
            Adicionar(r.Avisos, $"A loja {l.Code} - {l.Name} está vinculada ao centro de custo {l.LgCentroDeCustoCodigo}, que não veio na LG " +
                                "(confira LG:Empresas). Nada foi alterado nela.");
        return r;
    }
}

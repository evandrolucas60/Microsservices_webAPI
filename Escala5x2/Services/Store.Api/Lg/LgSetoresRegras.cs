using System.Text.RegularExpressions;
using Escala5x2.BuildingBlocks.Contracts.Stores;
using Escala5x2.BuildingBlocks.Shared.Lg;
using Store.Api.Domain;

namespace Store.Api.Lg;

/// <summary>Resultado de <see cref="LgSetoresRegras.Garantir"/>: o que gravar, o mapa órgão → setor e os eventos.</summary>
public sealed class LgSetoresResultado
{
    public Dictionary<int, Guid> Mapa { get; } = [];
    /// <summary>Setores novos (Origem = Lg) a incluir no banco.</summary>
    public List<Sector> Novos { get; } = [];
    public List<LgSetorGarantido> Detalhes { get; } = [];
    public List<string> Avisos { get; } = [];
    /// <summary>(setor, ação, nome anterior) para a auditoria: criado | adotado | renomeado | inativado | reativado.</summary>
    public List<(Sector Setor, string Acao, string? Anterior)> Alteracoes { get; } = [];
    public int Criados => Alteracoes.Count(a => a.Acao == "criado");
    public int Adotados => Alteracoes.Count(a => a.Acao == "adotado");
    public int Renomeados => Alteracoes.Count(a => a.Acao == "renomeado");
    public int Inativados => Alteracoes.Count(a => a.Acao == "inativado");
    public int Reativados => Alteracoes.Count(a => a.Acao == "reativado");
    /// <summary>Algo a gravar além da data da sincronização (setor criado, adotado, renomeado, inativado, reativado ou descrição LG nova).</summary>
    public bool Mudou { get; set; }
}

/// <summary>
/// Setores da loja a partir do OrgaoResponsavel da LG (ConsultarListaHistoricosVigenteNaDataPorDemanda). Para cada órgão:
/// <list type="number">
/// <item>casa pelo código (loja, <see cref="Sector.LgOrgaoCodigo"/>);</item>
/// <item>senão pelo nome normalizado (sem acento, maiúsculas e espaços repetidos): adota o setor manual com o mesmo nome
/// (preenche o código LG; o nome do gestor fica) — não duplica;</item>
/// <item>senão cria o setor com Origem = Lg e nome = descrição da LG como veio (maiúsculas, espaços normalizados — mesma
/// decisão das lojas, que usam o nome do centro de custo: nada de acento ou caixa inventados; ex.: "DEPOSITO").</item>
/// </list>
/// Nunca apaga setor. Renomeação: setor criado pela LG acompanha a descrição da LG (aviso no resumo e auditoria); setor
/// manual (inclusive adotado) nunca é renomeado — só aviso quando a descrição muda na LG. Nome que colidiria com outro
/// setor da loja também não é aplicado (aviso).
/// </summary>
public static partial class LgSetoresRegras
{
    public const int MaxNome = 100;

    [GeneratedRegex(@"\s+")]
    private static partial Regex Espacos();

    /// <summary>Chave de comparação de nomes de setor ("Depósito" = "DEPOSITO" = " deposito ").</summary>
    public static string Chave(string? nome) => LgXml.Normalizar(nome);

    /// <summary>Nome do setor criado pela LG: a descrição sem espaços nas pontas/repetidos, até 100 caracteres.</summary>
    public static string NomeDoOrgao(string? descricao)
    {
        var nome = Espacos().Replace(descricao?.Trim() ?? string.Empty, " ");
        return nome.Length > MaxNome ? nome[..MaxNome].TrimEnd() : nome;
    }

    /// <summary>
    /// Aplica os órgãos nos setores da loja (<paramref name="setores"/>: TODOS os setores da loja, rastreados; os novos são
    /// acrescentados à lista e a <see cref="LgSetoresResultado.Novos"/>). Idempotente: a segunda vez com os mesmos órgãos
    /// não muda nada.
    /// </summary>
    public static LgSetoresResultado Garantir(IEnumerable<LgOrgaoSetor> orgaos, List<Sector> setores, Guid storeId, DateTime agora)
    {
        var r = new LgSetoresResultado();
        // Mesmo código repetido no pedido: vale o primeiro com descrição
        var unicos = orgaos.GroupBy(o => o.Codigo)
            .Select(g => g.FirstOrDefault(o => !string.IsNullOrWhiteSpace(o.Descricao)) ?? g.First())
            .OrderBy(o => o.Codigo);

        foreach (var orgao in unicos)
        {
            var nomeLg = NomeDoOrgao(orgao.Descricao);
            var chave = Chave(nomeLg);
            var rotulo = $"{orgao.Codigo} {nomeLg}".Trim();
            if (orgao.Status is { } st && st != 0)
                r.Avisos.Add($"Órgão {rotulo} está com Status {st} na LG (inativo?): setor mantido para os colaboradores que ainda o têm.");

            // 1) Pelo código do órgão
            var setor = setores.FirstOrDefault(s => s.LgOrgaoCodigo == orgao.Codigo);
            if (setor is not null)
            {
                var acao = "existente";
                if (chave.Length > 0 && setor.LgOrgaoDescricao != nomeLg)
                {
                    if (setor.Origem == SectorOrigem.Lg && setor.Name != nomeLg)
                    {
                        if (setores.Any(s => s != setor && Chave(s.Name) == chave))
                        {
                            r.Avisos.Add($"Órgão {orgao.Codigo}: a descrição mudou na LG para \"{nomeLg}\", mas já existe outro setor com esse nome na loja; o setor continua \"{setor.Name}\".");
                        }
                        else
                        {
                            var anterior = setor.Name;
                            setor.Name = nomeLg;
                            acao = "renomeado";
                            r.Alteracoes.Add((setor, acao, anterior));
                            r.Avisos.Add($"Setor \"{anterior}\" renomeado para \"{nomeLg}\": a descrição do órgão {orgao.Codigo} mudou na LG.");
                        }
                    }
                    else if (setor.Origem == SectorOrigem.Manual && setor.LgOrgaoDescricao is not null && Chave(setor.LgOrgaoDescricao) != chave)
                    {
                        r.Avisos.Add($"Órgão {orgao.Codigo}: a descrição mudou na LG para \"{nomeLg}\"; o setor \"{setor.Name}\" foi cadastrado pelo gestor e mantém o nome.");
                    }
                    setor.LgOrgaoDescricao = nomeLg;
                    r.Mudou = true;
                }
                Marcar(r, orgao.Codigo, setor, acao, agora);
                continue;
            }

            if (chave.Length == 0)
            {
                r.Avisos.Add($"Órgão {orgao.Codigo} sem descrição na LG: nenhum setor criado (os colaboradores dele ficam sem setor).");
                continue;
            }

            // 2) Pelo nome normalizado: adota o setor sem código LG; com código de outro órgão, usa o mesmo setor (não duplica)
            var mesmoNome = setores.Where(s => Chave(s.Name) == chave).OrderBy(s => s.LgOrgaoCodigo is null ? 0 : 1).ToList();
            if (mesmoNome.FirstOrDefault() is { } existente)
            {
                if (existente.LgOrgaoCodigo is null)
                {
                    existente.LgOrgaoCodigo = orgao.Codigo;
                    existente.LgOrgaoDescricao = nomeLg;
                    r.Alteracoes.Add((existente, "adotado", null));
                    r.Mudou = true;
                    Marcar(r, orgao.Codigo, existente, "adotado", agora);
                }
                else
                {
                    r.Avisos.Add($"Órgãos {existente.LgOrgaoCodigo} e {orgao.Codigo} têm o mesmo nome na LG (\"{nomeLg}\"): usam o mesmo setor \"{existente.Name}\".");
                    Marcar(r, orgao.Codigo, existente, "existente", agora);
                }
                continue;
            }

            // 3) Cria
            var novo = new Sector
            {
                StoreId = storeId,
                Name = nomeLg,
                Origem = SectorOrigem.Lg,
                LgOrgaoCodigo = orgao.Codigo,
                LgOrgaoDescricao = nomeLg
            };
            setores.Add(novo);
            r.Novos.Add(novo);
            r.Alteracoes.Add((novo, "criado", null));
            r.Mudou = true;
            Marcar(r, orgao.Codigo, novo, "criado", agora);
        }
        return r;
    }

    private static void Marcar(LgSetoresResultado r, int codigo, Sector setor, string acao, DateTime agora)
    {
        setor.LgSincronizadoEm = agora;
        r.Mapa[codigo] = setor.Id;
        r.Detalhes.Add(new LgSetorGarantido(codigo, setor.Id, setor.Name, acao));
    }

    /// <summary>
    /// Executa <see cref="Garantir"/> com proteção contra corrida: duas sincronizações simultâneas podem tentar criar o
    /// mesmo setor; a segunda viola o índice único (loja, LgOrgaoCodigo) — <paramref name="violacaoUnica"/> — e repete
    /// do zero (relê os setores e casa pelo código). <paramref name="salvar"/> grava o resultado e, se falhar, descarta o
    /// que estava pendente (ChangeTracker.Clear no EF).
    /// </summary>
    public static Task<LgSetoresResultado> GarantirComRepeticaoAsync(
        IReadOnlyList<LgOrgaoSetor> orgaos, Guid storeId, DateTime agora,
        Func<CancellationToken, Task<List<Sector>>> carregar,
        Func<LgSetoresResultado, CancellationToken, Task> salvar,
        Func<Exception, bool> violacaoUnica,
        CancellationToken ct, int tentativas = 3) =>
        AplicarComRepeticaoAsync(setores => Garantir(orgaos, setores, storeId, agora), carregar, salvar, violacaoUnica, ct, tentativas);

    /// <summary>
    /// Mesma proteção de <see cref="GarantirComRepeticaoAsync"/> para qualquer regra sobre os setores da loja
    /// (<paramref name="aplicar"/>: garantir os órgãos do pedido do Employee.Api ou o catálogo inteiro com inativação).
    /// </summary>
    public static async Task<LgSetoresResultado> AplicarComRepeticaoAsync(
        Func<List<Sector>, LgSetoresResultado> aplicar,
        Func<CancellationToken, Task<List<Sector>>> carregar,
        Func<LgSetoresResultado, CancellationToken, Task> salvar,
        Func<Exception, bool> violacaoUnica,
        CancellationToken ct, int tentativas = 3)
    {
        for (var tentativa = 1; ; tentativa++)
        {
            var setores = await carregar(ct);
            var r = aplicar(setores);
            try
            {
                await salvar(r, ct);
                return r;
            }
            catch (Exception ex) when (tentativa < tentativas && violacaoUnica(ex))
            {
                // Outra sincronização gravou o mesmo órgão entre a leitura e a gravação: relê e casa pelo código
            }
        }
    }
}

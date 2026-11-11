using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Employee.Api.Domain;
using Escala5x2.BuildingBlocks.Shared.Auth;
using Escala5x2.BuildingBlocks.Shared.Lg;

namespace Employee.Api.Lg;

/// <summary>Regras da integração LG: situação, pendência de período, loja pelo centro de custo, setor, nome e LGPD.</summary>
public static partial class LgRegras
{
    /// <summary>SituacaoDoColaborador.Codigo 1 = ATIVIDADE NORMAL: único código disponível para a escala.</summary>
    public const int AtividadeNormal = 1;

    [GeneratedRegex(@"LOJA\s*(\d{1,3})", RegexOptions.CultureInvariant)]
    private static partial Regex LojaRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Espacos();

    /// <summary>Maiúsculas, sem acento e sem espaços repetidos (comparação de descrições da LG e nomes de setor).</summary>
    public static string Normalizar(string? texto) => LgXml.Normalizar(texto);

    // ---------- Situação ----------

    /// <summary>Código nulo (cadastro só da Escala) ou 1 = disponível para a escala.</summary>
    public static bool Disponivel(int? situacao) => situacao is null or AtividadeNormal;

    /// <summary>
    /// Tipo de ausência sugerido pela descrição da situação: "FERIAS" → Férias; "LICENCA"/"MATERNIDADE" → Licença;
    /// "AFAST"/"AUXILIO"/"DOENCA"/"ACIDENTE" e demais → Afastamento.
    /// </summary>
    public static UnavailabilityType TipoSugerido(string? descricao)
    {
        var d = Normalizar(descricao);
        if (d.Contains("FERIAS")) return UnavailabilityType.Ferias;
        if (d.Contains("LICENCA") || d.Contains("MATERNIDADE") || d.Contains("PATERNIDADE")) return UnavailabilityType.Licenca;
        return UnavailabilityType.Afastamento;
    }

    /// <summary>Início sugerido do período: DataInicioSituacaoAtual; sem ela, o dia da sincronização.</summary>
    public static DateOnly? InicioSugerido(Domain.Employee e) =>
        e.LgDataInicioSituacao ?? (e.LgSincronizadoEm is { } s ? DateOnly.FromDateTime(s) : null);

    /// <summary>
    /// Pendência de período: ativo, em férias/afastamento na LG (código ≠ 1) e SEM ausência registrada cobrindo o início
    /// da situação. Enquanto houver, o colaborador fica fora da fotografia de planejamento (geração de escala).
    /// </summary>
    public static bool PendenciaDePeriodo(Domain.Employee e)
    {
        if (!e.Active || Disponivel(e.LgSituacaoCodigo)) return false;
        if (InicioSugerido(e) is not { } inicio) return true;
        return !e.Unavailabilities.Any(u => u.Start <= inicio && u.End >= inicio);
    }

    // ---------- Loja e setor ----------

    /// <summary>
    /// Código normalizado da loja (<see cref="CodigoLoja.Normalizar"/>) do centro de custo: mapeamento LG:CentrosDeCusto
    /// pelo código; senão "LOJA nn" na descrição ("VAREJO LOJA 02 - COHAB" → "2"). null = sem loja reconhecida.
    /// </summary>
    public static string? CodigoLojaDoCentroDeCusto(LgItem? centro, IReadOnlyDictionary<string, string>? mapa = null)
    {
        if (centro is null) return null;
        if (mapa is not null && centro.Codigo is { Length: > 0 } codigo && mapa.TryGetValue(codigo.Trim(), out var loja))
            return CodigoLoja.Normalizar(loja);
        var m = LojaRegex().Match(Normalizar(centro.Descricao));
        return m.Success ? CodigoLoja.Normalizar(m.Groups[1].Value) : null;
    }

    /// <summary>
    /// Loja do colaborador pelo centro de custo, nesta ordem:
    /// <list type="number">
    /// <item>CentroDeCusto.Codigo = código LG da loja (Store.LgCentroDeCustoCodigo, preenchido pela sincronização de lojas);</item>
    /// <item>LG:CentrosDeCusto (centro de custo → Code da loja);</item>
    /// <item>"LOJA nn" na descrição casado com o Code pelo código normalizado — só com loja ainda sem código LG (loja
    /// vinculada a OUTRO centro de custo não é deste: ex. varejo e atacarejo com o mesmo número).</item>
    /// </list>
    /// Sem loja: <c>Aviso</c> preenchido quando o centro de custo parece loja (rode a sincronização de lojas / mapeie).
    /// </summary>
    public static (LojaEscala? Loja, string? Aviso) LojaDoCentroDeCusto(LgItem? centro, IReadOnlyList<LojaEscala> lojas,
        IReadOnlyDictionary<string, string>? mapa = null)
    {
        if (centro is null) return (null, null);
        var codigo = centro.Codigo?.Trim();
        var rotulo = $"{codigo} {centro.Descricao}".Trim();
        if (!string.IsNullOrEmpty(codigo)
            && lojas.FirstOrDefault(l => string.Equals(l.LgCentroDeCustoCodigo?.Trim(), codigo, StringComparison.Ordinal)) is { } vinculada)
            return (vinculada, null);

        if (mapa is not null && !string.IsNullOrEmpty(codigo) && mapa.TryGetValue(codigo, out var mapeada))
        {
            var normalizada = CodigoLoja.Normalizar(mapeada);
            var loja = lojas.FirstOrDefault(l => CodigoLoja.Normalizar(l.Code) == normalizada);
            return loja is not null
                ? (loja, null)
                : (null, $"Centro de custo {rotulo} mapeado para a loja {mapeada} (LG:CentrosDeCusto), que não existe no Store.Api: rode a sincronização de lojas ou cadastre a loja.");
        }

        var numero = CodigoLojaDoCentroDeCusto(centro);
        if (numero is null) return (null, null);
        var porCodigo = lojas.FirstOrDefault(l => CodigoLoja.Normalizar(l.Code) == numero);
        if (porCodigo is null)
            return (null, $"Centro de custo {rotulo} é da loja {numero}, que ainda não existe no Store.Api: rode a sincronização de lojas.");
        if (!string.IsNullOrWhiteSpace(porCodigo.LgCentroDeCustoCodigo))
            return (null, $"Centro de custo {rotulo}: a loja {porCodigo.Code} está vinculada ao centro de custo {porCodigo.LgCentroDeCustoCodigo} na LG; " +
                          "rode a sincronização de lojas ou mapeie o centro de custo (LG:Lojas:Mapeamento no Store.Api ou LG:CentrosDeCusto).");
        return (porCodigo, null);
    }

    /// <summary>
    /// Setor padrão pelo OrgaoResponsavel da LG (<paramref name="setorLg"/>: setor da loja garantido pelo Store.Api para o
    /// órgão; null = órgão vazio na LG). Sempre guarda o órgão atual no colaborador. Depois:
    /// <list type="bullet">
    /// <item>setores alterados manualmente (<see cref="Domain.Employee.SetoresManuais"/>): não mexe (a tela mostra a
    /// divergência com o setor da LG). Exceção da migração SetoresLg: marca sem autor/data e setor já igual ao da LG →
    /// passa a seguir a LG;</item>
    /// <item>segue a LG: setor = o do órgão (troca quando o órgão muda na LG); órgão vazio mantém o que tem (o
    /// colaborador novo fica sem setor).</item>
    /// </list>
    /// Os setores removidos voltam em <c>Removidos</c> (o chamador os tira do DbContext).
    /// </summary>
    public static (bool Mudou, IReadOnlyList<EmployeeSector> Removidos) AplicarSetorLg(Domain.Employee e, int? orgaoCodigo, string? orgaoDescricao, Guid? setorLg)
    {
        var descricao = orgaoDescricao?.Trim() is { Length: > 0 } d ? (d.Length > 100 ? d[..100] : d) : null;
        var mudou = e.LgOrgaoCodigo != orgaoCodigo || e.LgOrgaoDescricao != descricao || e.LgSetorId != setorLg;
        e.LgOrgaoCodigo = orgaoCodigo;
        e.LgOrgaoDescricao = descricao;
        e.LgSetorId = setorLg;

        if (e.SetoresManuais && e.SetoresAlteradosEm is null && e.SetoresAlteradosPor is null && setorLg is { } legado
            && e.Sectors.Count == 1 && e.Sectors[0].SectorId == legado)
        {
            e.SetoresManuais = false; // marcado só pela migração e já igual ao órgão: passa a seguir a LG
            mudou = true;
        }
        if (e.SetoresManuais || setorLg is not { } setor) return (mudou, []);
        if (e.Sectors.Count == 1 && e.Sectors[0].SectorId == setor) return (mudou, []);

        var removidos = e.Sectors.Where(s => s.SectorId != setor).ToList();
        e.Sectors.RemoveAll(removidos.Contains);
        if (e.Sectors.Count == 0) e.Sectors.Add(new EmployeeSector { EmployeeId = e.Id, SectorId = setor });
        return (true, removidos);
    }

    /// <summary>Setores alterados manualmente e diferentes do setor do órgão da LG (só o setor da LG).</summary>
    public static bool SetorDivergente(Domain.Employee e) =>
        e.SetoresManuais && e.LgSetorId is { } lg && !(e.Sectors.Count == 1 && e.Sectors[0].SectorId == lg);

    // ---------- Nome ----------

    private static readonly HashSet<string> Particulas = ["da", "das", "de", "do", "dos", "e", "di", "du", "del"];

    /// <summary>Title Case pt-BR: "EDILENE da silva da costa" → "Edilene da Silva da Costa" (partículas em minúsculas).</summary>
    public static string NomeProprio(string? nome)
    {
        if (string.IsNullOrWhiteSpace(nome)) return string.Empty;
        var cultura = CultureInfo.GetCultureInfo("pt-BR");
        var palavras = Espacos().Split(nome.Trim().ToLower(cultura));
        for (var i = 0; i < palavras.Length; i++)
        {
            var p = palavras[i];
            if (i > 0 && Particulas.Contains(p)) continue;
            // Nomes compostos com hífen/apóstrofo: cada parte com inicial maiúscula (D'Ávila, Ana-Maria)
            palavras[i] = string.Join("-", p.Split('-').Select(parte =>
                string.Join("'", parte.Split('\'').Select(x => x.Length == 0 ? x : char.ToUpper(x[0], cultura) + x[1..]))));
        }
        return string.Join(' ', palavras);
    }

    // ---------- LGPD ----------

    /// <summary>"88888888888" → "***.***.***-88". Vazio/inválido → null.</summary>
    public static string? MascararCpf(string? cpf)
    {
        var digitos = new string((cpf ?? string.Empty).Where(char.IsDigit).ToArray());
        return digitos.Length < 2 ? null : $"***.***.***-{digitos[^2..]}";
    }

    /// <summary>"1970-04-06T00:00:00" → "06/04" (só dia/mês). Inválida → null.</summary>
    public static string? MascararNascimento(string? data) =>
        DateTime.TryParse(data, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d.ToString("dd/MM", CultureInfo.InvariantCulture) : null;

    /// <summary>
    /// Registro LG para a tela conforme o papel: Admin e RH veem CPF e nascimento completos; Gestor, mascarados
    /// (***.***.***-88 e só dia/mês); demais papéis, sem esses campos. Devolve uma CÓPIA (o original não muda).
    /// </summary>
    public static JsonObject AplicarLgpd(JsonObject registro, string? papel)
    {
        var copia = (JsonObject)registro.DeepClone();
        if (papel is Roles.Admin or Roles.RH) return copia;
        if (copia["Pessoa"] is JsonObject pessoa)
        {
            var cpf = pessoa["Cpf"]?.GetValue<string>();
            var nascimento = pessoa["DataDeNascimento"]?.GetValue<string>();
            if (papel == Roles.Gestor)
            {
                if (pessoa.ContainsKey("Cpf")) pessoa["Cpf"] = MascararCpf(cpf);
                if (pessoa.ContainsKey("DataDeNascimento")) pessoa["DataDeNascimento"] = MascararNascimento(nascimento);
            }
            else
            {
                pessoa.Remove("Cpf");
                pessoa.Remove("DataDeNascimento");
            }
        }
        // Colaborador: a descrição da situação (ex.: "AUXILIO DOENCA") pode indicar dado de saúde → só "Afastado"
        if (papel is not (Roles.Gestor or Roles.Juridico) && copia["SituacaoDoColaborador"] is JsonObject situacao
            && situacao["Codigo"]?.ToString() != AtividadeNormal.ToString(CultureInfo.InvariantCulture))
        {
            situacao["Descricao"] = Escala5x2.BuildingBlocks.Contracts.Planning.UnavailabilityKinds.RotuloGenerico;
            situacao.Remove("Codigo");
        }
        return copia;
    }
}

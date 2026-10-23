namespace BuildingBlocks.Contracts.Identidade;

/// <summary>
/// Valores aceitos no cadastro do colaborador (setor e loja), validados pelo AuthAPI no registro.
/// ATENÇÃO: espelham as constantes SETOR e LOJAS de Frontend/src/data/constants.js e precisam
/// ficar iguais a elas (sem a opção vazia e sem o separador "-------"). Ao incluir/renomear um
/// setor ou loja, altere os dois lugares.
/// </summary>
public static class DadosCadastroColaborador
{
    public static readonly IReadOnlyList<string> Setores =
    [
        "Central de Notas",
        "Comercial",
        "Contabilidade",
        "Controladoria",
        "Cozinha",
        "Departamento de Pessoal",
        "Diretoria",
        "DPO",
        "DPP",
        "E-Commerce",
        "Financeiro",
        "GC/Price",
        "GP",
        "Infraestrutura",
        "Jurídico",
        "Logística",
        "Marketing",
        "Med. Trabalho",
        "Portaria",
        "Qualidade",
        "Recepção CD",
        "Recepção Prazeres",
        "SESMT",
        "T.I",
        // ---- setores de loja ----
        "Caixa Geral",
        "Cartão",
        "CPD",
        "Delivery",
        "Fiscal Lider",
        "Frente de Loja",
        "G.O",
        "Gerencia",
        "Lider Açougue",
        "Monitoração",
        "Padaria",
        "Ponto System",
        "Portaria Funcionario",
        "Portaria Recebimento",
        "Prevenção",
        "Televenda",
        "Telefonia",
    ];

    public static readonly IReadOnlyList<string> Lojas =
    [
        "CD",
        "01 - CABO",
        "02 - COHAB",
        "03 - PONTEZINHA",
        "04 - PRAZERES",
        "05 - CAVALEIRO",
        "07 - CAMARAGIBE",
        "08 - BEBERIBE",
        "14 - IGARASSU",
        "16 - AREIAS",
        "17 - CEASA",
        "18 - ABREU CENTRO",
        "19 - VÁRZEA",
        "21 - MASSANGANA",
        "22 - GAIBU",
        "23 - PORTO DE GALINHAS",
        "24 - COSTA DOURADA",
        "26 - CAXANGÁ",
        "27 - NOSSA SENHORA DO Ó",
        "28 - ASA BRANCA",
        "30 - CANDEIAS",
    ];

    /// <summary>Ramal interno: só dígitos, de 2 a 6.</summary>
    public const string RamalRegex = @"^\d{2,6}$";

    /// <summary>Telefone com DDD: celular (81) 99999-9999 ou fixo (81) 3333-4444.</summary>
    public const string TelefoneRegex = @"^\(\d{2}\) \d{4,5}-\d{4}$";

    public static bool SetorValido(string? setor) =>
        !string.IsNullOrWhiteSpace(setor) && Setores.Contains(setor.Trim(), StringComparer.Ordinal);

    public static bool LojaValida(string? loja) =>
        !string.IsNullOrWhiteSpace(loja) && Lojas.Contains(loja.Trim(), StringComparer.Ordinal);
}

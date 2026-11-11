namespace Store.Tests;

/// <summary>Respostas de ConsultarListaPorDemanda (OrgaoResponsavelCompleto) no formato real enviado pela LG (namespace api/v1).</summary>
public static class LgOrgaosFixtures
{
    public const string SoapAction = "lg.com.br/api/v1/ServicoDeOrgaoResponsavel/ConsultarListaPorDemanda";

    /// <summary>Habilitações do exemplo da LG: 1, 2, 4, 6, 7, 102..106.</summary>
    public static readonly int[] EmpresasExemplo = [1, 2, 4, 6, 7, 102, 103, 104, 105, 106];

    public static string Orgao(int codigo, string descricao, int status = 0, int[]? empresas = null)
    {
        var habilitacoes = string.Concat((empresas ?? EmpresasExemplo).Select(e =>
            $"<a:ObjetoComCodigoNumerico><a:Codigo>{e}</a:Codigo><a:CodigoDeIntegracao i:nil=\"true\"/></a:ObjetoComCodigoNumerico>"));
        return $"""
            <a:OrgaoResponsavelCompleto>
             <a:Codigo>{codigo}</a:Codigo><a:CodigoCnae/><a:Descricao>{descricao}</a:Descricao><a:Empresa i:nil="true"/>
             <a:Endereco><a:Bairro i:nil="true"/><a:CaixaPostal i:nil="true"/><a:Cep i:nil="true"/><a:CepCaixaPostal i:nil="true"/><a:Complemento i:nil="true"/><a:Logradouro i:nil="true"/><a:Municipio i:nil="true"/><a:Numero i:nil="true"/>
               <a:TipoDeLogradouro><a:Codigo>0</a:Codigo><a:CodigoDeIntegracao i:nil="true"/><a:Descricao i:nil="true"/><a:CodigoESocial i:nil="true"/></a:TipoDeLogradouro></a:Endereco>
             <a:Identificacao/>
             <a:ListaHabilitacaoPorEmpresas>{habilitacoes}</a:ListaHabilitacaoPorEmpresas>
             <a:RazaoSocial/><a:Sigla/><a:Status>{status}</a:Status><a:TipoDeIdentificacao i:nil="true"/><a:ListaValorDaInformacaoAdicional i:nil="true"/>
            </a:OrgaoResponsavelCompleto>
            """;
    }

    /// <summary>Os 4 órgãos do exemplo da LG (página 1 de 2, TotalGeral 63).</summary>
    public static readonly string[] Exemplo =
    [
        Orgao(1, "GERENCIA"),
        Orgao(2, "FRENTE DE LOJA"),
        Orgao(3, "AÇOUGUE"),
        Orgao(4, "FRIOS")
    ];

    public static string Resposta(int pagina, int totalPaginas, int totalGeral, params string[] orgaos) => $"""
        <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"><s:Body>
        <ConsultarListaPorDemandaResponse xmlns="lg.com.br/api/v1">
        <ConsultarListaPorDemandaResult xmlns:a="lg.com.br/api/dto/v1" xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
         <a:CodigoDoErro i:nil="true"/><a:Mensagens i:nil="true" xmlns:b="http://schemas.microsoft.com/2003/10/Serialization/Arrays"/><a:Tipo>0</a:Tipo>
         <a:Retorno>{string.Concat(orgaos)}</a:Retorno>
         <a:NumeroDaPagina>{pagina}</a:NumeroDaPagina><a:QuantidadePorPagina>50</a:QuantidadePorPagina><a:TotalDePaginas>{totalPaginas}</a:TotalDePaginas><a:TotalGeral>{totalGeral}</a:TotalGeral>
        </ConsultarListaPorDemandaResult></ConsultarListaPorDemandaResponse></s:Body></s:Envelope>
        """;
}

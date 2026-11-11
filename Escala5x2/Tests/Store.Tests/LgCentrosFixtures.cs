using System.Net;
using System.Text;

namespace Store.Tests;

/// <summary>Respostas de ConsultarListaCentroDeCustoCompletoPorDemanda no formato real (resposta enviada pela LG, bem formada).</summary>
public static class LgCentrosFixtures
{
    private const string Habilitacoes = """
        <a:Habilitacoes><a:Empresas>
          <a:ObjetoComCodigoNumerico><a:Codigo>4</a:Codigo><a:CodigoDeIntegracao i:nil="true"/></a:ObjetoComCodigoNumerico>
          <a:ObjetoComCodigoNumerico><a:Codigo>2</a:Codigo><a:CodigoDeIntegracao i:nil="true"/></a:ObjetoComCodigoNumerico>
          <a:ObjetoComCodigoNumerico><a:Codigo>7</a:Codigo><a:CodigoDeIntegracao i:nil="true"/></a:ObjetoComCodigoNumerico>
          <a:ObjetoComCodigoNumerico><a:Codigo>103</a:Codigo><a:CodigoDeIntegracao i:nil="true"/></a:ObjetoComCodigoNumerico>
          <a:ObjetoComCodigoNumerico><a:Codigo>105</a:Codigo><a:CodigoDeIntegracao i:nil="true"/></a:ObjetoComCodigoNumerico>
          <a:ObjetoComCodigoNumerico><a:Codigo>1</a:Codigo><a:CodigoDeIntegracao i:nil="true"/></a:ObjetoComCodigoNumerico>
          <a:ObjetoComCodigoNumerico><a:Codigo>6</a:Codigo><a:CodigoDeIntegracao i:nil="true"/></a:ObjetoComCodigoNumerico>
          <a:ObjetoComCodigoNumerico><a:Codigo>104</a:Codigo><a:CodigoDeIntegracao i:nil="true"/></a:ObjetoComCodigoNumerico>
          <a:ObjetoComCodigoNumerico><a:Codigo>102</a:Codigo><a:CodigoDeIntegracao i:nil="true"/></a:ObjetoComCodigoNumerico>
          <a:ObjetoComCodigoNumerico><a:Codigo>106</a:Codigo><a:CodigoDeIntegracao i:nil="true"/></a:ObjetoComCodigoNumerico>
        </a:Empresas><a:Estabelecimentos/><a:UnidadesOrganizacionais/></a:Habilitacoes>
        """;

    /// <summary>Um CentroDeCustoCompleto; <paramref name="dataFim"/> null = i:nil.</summary>
    public static string Centro(string codigo, string descricao, int status = 0, string? dataFim = null, string? adicionais = null) => $"""
        <a:CentroDeCustoCompleto>
         <a:Codigo>{codigo}</a:Codigo><a:CodigoDeIntegracao i:nil="true"/><a:Descricao>{descricao}</a:Descricao><a:Status>{status}</a:Status>
         <a:CodigoDeSubdivisao i:nil="true"/><a:DataInicio>1900-01-01T00:00:00</a:DataInicio><a:Empresa i:nil="true"/>
         {Habilitacoes}
         {adicionais ?? "<a:ListaValorDaInformacaoAdicional i:nil=\"true\"/>"}
         <a:PermiteCadastrarColaborador>true</a:PermiteCadastrarColaborador><a:UtilizarValorPadraoInformacaoAdicional>false</a:UtilizarValorPadraoInformacaoAdicional>
         {(dataFim is null ? "<a:DataFim i:nil=\"true\"/>" : $"<a:DataFim>{dataFim}</a:DataFim>")}
        </a:CentroDeCustoCompleto>
        """;

    public const string Adicionais = """
        <a:ListaValorDaInformacaoAdicional><a:ValorDaInformacaoAdicional><a:Codigo>83</a:Codigo><a:TipoDaEntidade>1000</a:TipoDaEntidade><a:Valor>0000000071</a:Valor><a:OpcoesSelecionadas xmlns:b="http://schemas.microsoft.com/2003/10/Serialization/Arrays"/></a:ValorDaInformacaoAdicional></a:ListaValorDaInformacaoAdicional>
        """;

    /// <summary>Os 4 centros de custo do exemplo da LG (página 1 de 2, TotalGeral 84).</summary>
    public static readonly string[] Exemplo =
    [
        Centro("000001", "INDUSTRIA ALIMENTICIA SELO REAL LTDA EPP"),
        Centro("000010401", "RESIDÊNCIA"),
        Centro("001020100", "PRESIDENCIA"),
        Centro("201000", "VAREJO LOJA 01 - CABO", adicionais: Adicionais)
    ];

    public static string Resposta(int pagina, int totalPaginas, int totalGeral, params string[] centros) => $"""
        <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"><s:Body>
        <ConsultarListaCentroDeCustoCompletoPorDemandaResponse xmlns="lg.com.br/api/v2">
        <ConsultarListaCentroDeCustoCompletoPorDemandaResult xmlns:a="lg.com.br/api/dto/v1" xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
         <a:CodigoDoErro i:nil="true"/><a:Mensagens i:nil="true" xmlns:b="http://schemas.microsoft.com/2003/10/Serialization/Arrays"/><a:Tipo>0</a:Tipo>
         <a:Retorno>{string.Concat(centros)}</a:Retorno>
         <a:NumeroDaPagina>{pagina}</a:NumeroDaPagina><a:QuantidadePorPagina>50</a:QuantidadePorPagina><a:TotalDePaginas>{totalPaginas}</a:TotalDePaginas><a:TotalGeral>{totalGeral}</a:TotalGeral>
        </ConsultarListaCentroDeCustoCompletoPorDemandaResult></ConsultarListaCentroDeCustoCompletoPorDemandaResponse></s:Body></s:Envelope>
        """;

    /// <summary>Como veio colado do exemplo: um &lt;/a:CentroDeCustoCompleto&gt; sobrando no fim (XML mal formado).</summary>
    public static string RespostaMalFormada() =>
        Resposta(1, 2, 84, Exemplo).Replace("</a:Retorno>", "</a:CentroDeCustoCompleto></a:Retorno>");
}

/// <summary>HttpMessageHandler falso: responde pela função e guarda as requisições (URL, corpo e SOAPAction).</summary>
public sealed class FakeLgHandler(Func<HttpRequestMessage, string, int, HttpResponseMessage> responder) : HttpMessageHandler
{
    public List<(Uri? Url, string Body, string? SoapAction)> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request.RequestUri, body, request.Headers.TryGetValues("SOAPAction", out var v) ? v.FirstOrDefault() : null));
        return responder(request, body, Requests.Count);
    }

    public static HttpResponseMessage Xml(string xml, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(xml, Encoding.UTF8, "text/xml") };
}

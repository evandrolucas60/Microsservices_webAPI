using System.Net;
using System.Text;

namespace Employee.Tests;

/// <summary>Respostas da LG no formato real (dados fictícios: CPF 88888888888 e afins).</summary>
public static class LgFixtures
{
    private static string Contrato(string matricula, string nome, string cpf, string cargoCodigo, string cargo, string ccCodigo, string cc,
        string situacaoCodigo, string situacao, string inicioSituacao, string orgaoCodigo, string orgao) => $"""
        <a:ContratoDeTrabalhoParcial>
          <a:DataAdmissao>1997-06-01T00:00:00</a:DataAdmissao>
          <a:DataInicioSituacaoAtual>{inicioSituacao}</a:DataInicioSituacaoAtual>
          <a:Matricula>{matricula}</a:Matricula>
          <a:VinculoEmpregaticio>10</a:VinculoEmpregaticio>
          <a:Cargo><a:Codigo>{cargoCodigo}</a:Codigo><a:CodigoDeIntegracao i:nil="true"/><a:Descricao>{cargo}</a:Descricao>
            <a:Cbo><a:Codigo>521110</a:Codigo><a:CodigoDeIntegracao i:nil="true"/><a:Descricao>VENDEDOR DE COMÉRCIO VAREJISTA</a:Descricao></a:Cbo></a:Cargo>
          <a:CentroDeCusto><a:Codigo>{ccCodigo}</a:Codigo><a:CodigoDeIntegracao i:nil="true"/><a:Descricao>{cc}</a:Descricao></a:CentroDeCusto>
          <a:Empresa><a:Codigo>1</a:Codigo><a:CodigoDeIntegracao i:nil="true"/><a:Descricao>SUPERMERCADOS DA FAMÍLIA SA.</a:Descricao></a:Empresa>
          <a:Estabelecimento><a:Codigo>39</a:Codigo><a:CodigoDeIntegracao i:nil="true"/><a:Descricao>SUPERMERCADO DA FAMILIA S.A</a:Descricao></a:Estabelecimento>
          <a:NaturezaProfissional i:nil="true"/>
          <a:Pessoa><a:Cpf>{cpf}</a:Cpf><a:DataDeNascimento>1970-04-06T00:00:00</a:DataDeNascimento><a:Nome>{nome}</a:Nome><a:PessoaId>9726</a:PessoaId></a:Pessoa>
          <a:SituacaoDoColaborador><a:Codigo>{situacaoCodigo}</a:Codigo><a:CodigoDeIntegracao i:nil="true"/><a:Descricao>{situacao}</a:Descricao></a:SituacaoDoColaborador>
          <a:UnidadeOrganizacional><a:Codigo>19</a:Codigo><a:CodigoDeIntegracao i:nil="true"/><a:Descricao>EQUIPE LOJA COHAB</a:Descricao></a:UnidadeOrganizacional>
          <a:Posicao i:nil="true"/>
          <a:Sindicato><a:Codigo>1</a:Codigo><a:CodigoDeIntegracao i:nil="true"/><a:Descricao>SESSEPE</a:Descricao></a:Sindicato>
          <a:CategoriaDoColaborador>101</a:CategoriaDoColaborador><a:MarcaPonto>true</a:MarcaPonto><a:TipoPonto>2</a:TipoPonto>
          <a:OrgaoResponsavel><a:Codigo>{orgaoCodigo}</a:Codigo><a:CodigoDeIntegracao i:nil="true"/><a:Status>0</a:Status><a:Descricao>{orgao}</a:Descricao></a:OrgaoResponsavel>
          <a:NumeroDoProcessoSeletivo i:nil="true"/>
        </a:ContratoDeTrabalhoParcial>
        """;

    public static readonly string Edilene = Contrato("37", "EDILENE da silva da costa", "88888888888", "452", "OPERADOR DE LOJA", "202000",
        "VAREJO LOJA 02 - COHAB", "1", "ATIVIDADE NORMAL", "2026-07-01T00:00:00", "2", "FRENTE DE LOJA");
    public static readonly string Joyce = Contrato("6576", "JOYCE BARBOSA DA SILVA", "77777777777", "224", "ENCARREGADO TELEFONIA/ELETRO", "203000",
        "VAREJO LOJA 03 - PONTEZINHA", "8", "FERIAS NOR AD 13 SALARIO", "2026-10-05T00:00:00", "13", "TELEFONIA");
    public static readonly string Luiz = Contrato("9489", "LUIZ FELIPE PEREIRA SILVA", "66666666666", "587", "ENC DE DEPOSITO ATACAREJO I", "227000",
        "VAREJO LOJA 27 NOSSA SENHORA DO O", "9", "FERIAS NORMAIS", "2026-10-01T00:00:00", "25", "DEPOSITO");

    /// <summary>Colaborador em ATIVIDADE NORMAL no centro de custo e órgão responsável dados (órgão null = i:nil).</summary>
    public static string Colaborador(string matricula, string ccCodigo, string cc, string? orgaoCodigo, string? orgao)
    {
        var xml = Contrato(matricula, $"COLABORADOR {matricula}", "55555555555", "452", "OPERADOR DE LOJA", ccCodigo, cc, "1",
            "ATIVIDADE NORMAL", "2026-01-01T00:00:00", orgaoCodigo ?? "0", orgao ?? "");
        if (orgaoCodigo is not null) return xml;
        var inicio = xml.IndexOf("<a:OrgaoResponsavel>", StringComparison.Ordinal);
        var fim = xml.IndexOf("</a:OrgaoResponsavel>", StringComparison.Ordinal) + "</a:OrgaoResponsavel>".Length;
        return xml[..inicio] + "<a:OrgaoResponsavel i:nil=\"true\"/>" + xml[fim..];
    }

    public static string Resposta(int pagina, int totalPaginas, int totalGeral, params string[] contratos) => $"""
        <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"><s:Body>
        <ConsultarListaHistoricosVigenteNaDataPorDemandaResponse xmlns="lg.com.br/api/v2">
        <ConsultarListaHistoricosVigenteNaDataPorDemandaResult xmlns:a="lg.com.br/api/dto/v1" xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
         <a:CodigoDoErro i:nil="true"/><a:Mensagens i:nil="true"/><a:Tipo>0</a:Tipo>
         <a:Retorno>{string.Concat(contratos)}</a:Retorno>
         <a:NumeroDaPagina>{pagina}</a:NumeroDaPagina><a:QuantidadePorPagina>50</a:QuantidadePorPagina><a:TotalDePaginas>{totalPaginas}</a:TotalDePaginas><a:TotalGeral>{totalGeral}</a:TotalGeral>
        </ConsultarListaHistoricosVigenteNaDataPorDemandaResult></ConsultarListaHistoricosVigenteNaDataPorDemandaResponse></s:Body></s:Envelope>
        """;

    public static string RespostaComErro(string tipo, string codigo, string mensagem) => $"""
        <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"><s:Body>
        <ConsultarListaHistoricosVigenteNaDataPorDemandaResponse xmlns="lg.com.br/api/v2">
        <ConsultarListaHistoricosVigenteNaDataPorDemandaResult xmlns:a="lg.com.br/api/dto/v1" xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
         <a:CodigoDoErro>{codigo}</a:CodigoDoErro><a:Mensagens xmlns:b="http://schemas.microsoft.com/2003/10/Serialization/Arrays"><b:string>{mensagem}</b:string></a:Mensagens><a:Tipo>{tipo}</a:Tipo>
         <a:Retorno i:nil="true"/>
        </ConsultarListaHistoricosVigenteNaDataPorDemandaResult></ConsultarListaHistoricosVigenteNaDataPorDemandaResponse></s:Body></s:Envelope>
        """;

    public const string Fault = """
        <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"><s:Body><s:Fault>
        <faultcode xmlns:a="http://schemas.microsoft.com/ws/2005/05/addressing/none">a:ActionNotSupported</faultcode>
        <faultstring xml:lang="pt-BR">A mensagem com Action 'x' não pode ser processada.</faultstring>
        </s:Fault></s:Body></s:Envelope>
        """;
}

/// <summary>HttpMessageHandler falso: responde pela função e guarda as requisições (corpo e SOAPAction).</summary>
public sealed class FakeLgHandler(Func<HttpRequestMessage, string, int, HttpResponseMessage> responder) : HttpMessageHandler
{
    public List<(string Body, string? SoapAction)> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((body, request.Headers.TryGetValues("SOAPAction", out var v) ? v.FirstOrDefault() : null));
        return responder(request, body, Requests.Count);
    }

    public static HttpResponseMessage Xml(string xml, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(xml, Encoding.UTF8, "text/xml") };
}

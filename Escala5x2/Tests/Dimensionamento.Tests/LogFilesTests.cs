using Escala5x2.BuildingBlocks.Shared.Web;
using Xunit;

namespace Dimensionamento.Tests;

/// <summary>
/// Download de logs (BuildingBlocks.Shared, referenciado por este projeto via Dimensionamento.Api): o arquivo só é
/// resolvido dentro da pasta configurada do serviço, sem path traversal; e o CSV no formato do Excel em português.
/// </summary>
public sealed class LogFilesTests : IDisposable
{
    private readonly string _raiz = Path.Combine(Path.GetTempPath(), $"e5x2-logs-{Guid.NewGuid():N}");
    private readonly Dictionary<string, string> _pastas;

    public LogFilesTests()
    {
        var pasta = Path.Combine(_raiz, "logs");
        Directory.CreateDirectory(pasta);
        File.WriteAllText(Path.Combine(pasta, "Scheduling.Api-20261029.log"), "linha 1\n");
        File.WriteAllText(Path.Combine(pasta, "Scheduling.Api-20261028_001.log"), "linha 1\n");
        File.WriteAllText(Path.Combine(pasta, "outro.txt"), "não é log");
        File.WriteAllText(Path.Combine(_raiz, "segredo.log"), "fora da pasta");
        _pastas = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Scheduling.Api"] = pasta };
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_raiz, recursive: true);
        }
        catch (IOException)
        {
            // limpeza best-effort
        }
    }

    [Fact]
    public void Lista_so_arquivos_log_com_a_data_do_nome()
    {
        var lista = LogFiles.Listar(_pastas, null, null, null);
        Assert.Equal(2, lista.Count);
        Assert.Equal(new DateOnly(2026, 10, 29), lista[0].Data);
        Assert.Single(LogFiles.Listar(_pastas, "Scheduling.Api", new DateOnly(2026, 10, 28), new DateOnly(2026, 10, 28)));
        Assert.Empty(LogFiles.Listar(_pastas, "Store.Api", null, null));
    }

    [Theory]
    [InlineData("Scheduling.Api", "../segredo.log")]
    [InlineData("Scheduling.Api", "..\\segredo.log")]
    [InlineData("Scheduling.Api", "..")]
    [InlineData("Scheduling.Api", "outro.txt")]
    [InlineData("Scheduling.Api", "C:\\Windows\\win.ini")]
    [InlineData("Scheduling.Api", "/etc/passwd")]
    [InlineData("Scheduling.Api", "inexistente.log")]
    [InlineData("Desconhecido", "Scheduling.Api-20261029.log")]
    [InlineData("../logs", "Scheduling.Api-20261029.log")]
    public void Recusa_caminhos_fora_da_pasta_ou_invalidos(string servico, string arquivo) =>
        Assert.Null(LogFiles.Resolver(_pastas, servico, arquivo));

    [Fact]
    public void Resolve_arquivo_valido()
    {
        var caminho = LogFiles.Resolver(_pastas, "scheduling.api", "Scheduling.Api-20261029.log");
        Assert.NotNull(caminho);
        Assert.Equal(Path.Combine(_pastas["Scheduling.Api"], "Scheduling.Api-20261029.log"), caminho);
    }

    [Fact]
    public void Csv_escapa_separador_aspas_e_formula()
    {
        Assert.Equal("\"a;b\"", Csv.Campo("a;b"));
        Assert.Equal("\"diz \"\"oi\"\"\"", Csv.Campo("diz \"oi\""));
        Assert.Equal("'=1+1", Csv.Campo("=1+1"));
        Assert.Equal("-5", Csv.Campo("-5"));
        Assert.Equal("1,5", Csv.Numero(1.5));
    }
}

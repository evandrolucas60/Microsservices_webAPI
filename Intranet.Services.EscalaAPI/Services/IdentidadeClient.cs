using System.Globalization;
using System.Text;
using BuildingBlocks.Shared.Messaging;

namespace Intranet.Services.EscalaAPI.Services;

/// <summary>Colaborador do setor da escala (vindo do IdentidadeAPI).</summary>
public record ColaboradorInfo(Guid MemberId, string? Nome, string? Email, string? Cargo, string? Departamento, string? Foto);

/// <summary>
/// Colaboradores da T.I (quem pode ser escalado), consultados no IdentidadeAPI pelo RabbitMQ
/// (requisição/resposta do MassTransit). Lança ServicoIndisponivelException se não houver resposta.
/// </summary>
public class IdentidadeClient
{
    private readonly IIdentidadeGateway _identidade;
    private readonly IConfiguration _configuration;

    public IdentidadeClient(IIdentidadeGateway identidade, IConfiguration configuration)
    {
        _identidade = identidade;
        _configuration = configuration;
    }

    /// <summary>Setor responsável pelas escalas (configurável em Escala:Departamento).</summary>
    public string Departamento => _configuration["Escala:Departamento"] ?? "T.I";

    /// <summary>Compara setores ignorando pontos, espaços, acentos e maiúsculas ("T.I" = "TI").</summary>
    public bool IsDepartamentoDaEscala(string? departamento)
    {
        static string Normalize(string? value)
        {
            var decomposed = (value ?? string.Empty).Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder();
            foreach (var c in decomposed)
            {
                if (char.IsLetterOrDigit(c) && CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                    sb.Append(char.ToUpperInvariant(c));
            }
            return sb.ToString();
        }

        var normalized = Normalize(departamento);
        return normalized.Length > 0 && (normalized == Normalize(Departamento) || normalized == "TECNOLOGIADAINFORMACAO");
    }

    /// <summary>Colaboradores do setor da escala (T.I).</summary>
    public async Task<List<ColaboradorInfo>> GetColaboradoresDoSetorAsync(string? bearerToken, CancellationToken ct)
    {
        var colaboradores = await _identidade.ObterColaboradoresPorDepartamentoAsync(Departamento, ct);
        return colaboradores
            .Where(c => IsDepartamentoDaEscala(c.Departamento))
            .Select(c => new ColaboradorInfo(c.MemberId, c.Nome, c.Email, c.Cargo, c.Departamento, c.Foto))
            .ToList();
    }
}

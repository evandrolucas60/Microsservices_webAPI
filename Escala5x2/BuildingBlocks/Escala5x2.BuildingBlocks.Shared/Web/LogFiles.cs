using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using Escala5x2.BuildingBlocks.Shared.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Net.Http.Headers;

namespace Escala5x2.BuildingBlocks.Shared.Web;

/// <summary>Arquivo de log disponível para download. <paramref name="Data"/>: dia do arquivo (do nome AAAAMMDD).</summary>
public sealed record LogFileInfo(string Servico, string Arquivo, DateOnly? Data, long TamanhoBytes, DateTime ModificadoEm);

/// <summary>Pasta de logs de um serviço. <paramref name="Disponivel"/> = false quando a pasta não existe neste servidor.</summary>
public sealed record LogFolderInfo(string Servico, bool Disponivel);

public sealed record LogFilesResponse(IReadOnlyList<LogFolderInfo> Servicos, IReadOnlyList<LogFileInfo> Arquivos);

/// <summary>
/// Download dos arquivos de log do Serilog (só Admin = superAdmin da intranet). O <c>AddSharedSerilog</c> da intranet grava,
/// quando o appsettings não tem "Serilog:WriteTo", em <c>{ContentRoot}/logs/{serviço}-AAAAMMDD.log</c> (rolagem diária,
/// 14 dias, 50 MB por arquivo: <c>{serviço}-AAAAMMDD_001.log</c> ...). Cada serviço só enxerga a própria pasta, então cada
/// API expõe a sua em <c>{prefixo}/logs</c> (o prefixo é o que o gateway já roteia: /api/escala5x2/{área}/logs).
/// <para>
/// Configuração (opcional): "Logs:Pasta" troca a pasta do próprio serviço (relativa ao ContentRoot ou absoluta — use quando
/// o "Serilog:WriteTo" do appsettings gravar em outro lugar); "Logs:PastasExtras:{Serviço}" publica a pasta de outro processo
/// do mesmo servidor sem HTTP próprio (ex.: o Optimization.Worker pelo Scheduling.Api).
/// </para>
/// Segurança: o serviço vem da lista configurada (nunca do caminho), o nome do arquivo só aceita letras, números, ".", "_" e
/// "-" terminando em ".log", e o caminho resolvido precisa ficar dentro da pasta (sem path traversal nem links).
/// </summary>
public static partial class LogFiles
{
    public const string ConfigPasta = "Logs:Pasta";
    public const string ConfigPastasExtras = "Logs:PastasExtras";
    /// <summary>Limite de arquivos num zip (14 dias × alguns serviços cabem com folga).</summary>
    public const int MaxArquivosZip = 200;

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,200}\.log$")]
    private static partial Regex NomeValido();

    [GeneratedRegex(@"-(\d{8})(?:_\d+)?\.log$")]
    private static partial Regex DataNoNome();

    /// <summary>Pastas de log visíveis por este serviço: nome do serviço → caminho absoluto (a do próprio serviço primeiro).</summary>
    public static IReadOnlyDictionary<string, string> Pastas(IConfiguration configuration, IHostEnvironment environment)
    {
        var raiz = environment.ContentRootPath;
        var pastas = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [environment.ApplicationName] = Path.GetFullPath(Path.Combine(raiz, configuration[ConfigPasta] is { Length: > 0 } p ? p : "logs"))
        };
        foreach (var extra in configuration.GetSection(ConfigPastasExtras).GetChildren())
        {
            if (!string.IsNullOrWhiteSpace(extra.Value) && !pastas.ContainsKey(extra.Key))
                pastas[extra.Key] = Path.GetFullPath(Path.Combine(raiz, extra.Value));
        }
        return pastas;
    }

    /// <summary>Arquivos .log das pastas (filtro opcional por serviço e por dia), mais recentes primeiro.</summary>
    public static IReadOnlyList<LogFileInfo> Listar(IReadOnlyDictionary<string, string> pastas, string? servico, DateOnly? de, DateOnly? ate)
    {
        var lista = new List<LogFileInfo>();
        foreach (var (nome, pasta) in pastas)
        {
            if (!string.IsNullOrWhiteSpace(servico) && !string.Equals(nome, servico, StringComparison.OrdinalIgnoreCase)) continue;
            if (!Directory.Exists(pasta)) continue;
            foreach (var caminho in Directory.EnumerateFiles(pasta, "*.log", SearchOption.TopDirectoryOnly))
            {
                var info = new FileInfo(caminho);
                if (!NomeValido().IsMatch(info.Name) || info.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                var data = DataDoArquivo(info.Name);
                var dia = data ?? DateOnly.FromDateTime(info.LastWriteTime);
                if (de is { } d && dia < d) continue;
                if (ate is { } a && dia > a) continue;
                lista.Add(new LogFileInfo(nome, info.Name, data, info.Length, info.LastWriteTimeUtc));
            }
        }
        return lista.OrderByDescending(f => f.Data).ThenBy(f => f.Servico).ThenByDescending(f => f.Arquivo, StringComparer.Ordinal).ToList();
    }

    /// <summary>Caminho seguro do arquivo dentro da pasta do serviço, ou null (serviço desconhecido, nome inválido, fora da pasta, inexistente).</summary>
    public static string? Resolver(IReadOnlyDictionary<string, string> pastas, string? servico, string? arquivo)
    {
        if (string.IsNullOrWhiteSpace(servico) || string.IsNullOrWhiteSpace(arquivo)) return null;
        if (!pastas.TryGetValue(servico, out var pasta)) return null;
        if (!NomeValido().IsMatch(arquivo) || arquivo.Contains("..", StringComparison.Ordinal) || Path.GetFileName(arquivo) != arquivo) return null;
        var caminho = Path.GetFullPath(Path.Combine(pasta, arquivo));
        var dentro = string.Equals(Path.GetDirectoryName(caminho)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            pasta.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        if (!dentro) return null;
        var info = new FileInfo(caminho);
        return info.Exists && !info.Attributes.HasFlag(FileAttributes.ReparsePoint) ? caminho : null;
    }

    private static DateOnly? DataDoArquivo(string nome) =>
        DataNoNome().Match(nome) is { Success: true } m &&
        DateOnly.TryParseExact(m.Groups[1].Value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d : null;

    /// <summary>
    /// Leitura compartilhada: o Serilog mantém o arquivo do dia aberto para escrita (e a retenção pode apagá-lo).
    /// </summary>
    private static FileStream Abrir(string caminho) => new(caminho, new FileStreamOptions
    {
        Mode = FileMode.Open,
        Access = FileAccess.Read,
        Share = FileShare.ReadWrite | FileShare.Delete,
        Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
        BufferSize = 0,
    });

    /// <summary>Copia exatamente <paramref name="tamanho"/> bytes: o arquivo do dia cresce durante o download.</summary>
    private static async Task CopiarAsync(Stream origem, Stream destino, long tamanho, CancellationToken ct)
    {
        var buffer = new byte[81920];
        var restante = tamanho;
        while (restante > 0)
        {
            var lidos = await origem.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, restante)), ct);
            if (lidos == 0) break;
            await destino.WriteAsync(buffer.AsMemory(0, lidos), ct);
            restante -= lidos;
        }
    }

    private static string Anexo(string nome)
    {
        var disposition = new ContentDispositionHeaderValue("attachment");
        disposition.SetHttpFileName(nome);
        return disposition.ToString();
    }

    /// <summary>
    /// GET {prefixo}/logs?servico=&amp;de=&amp;ate= (lista), GET {prefixo}/logs/{servico}/{arquivo} (download em streaming) e
    /// GET {prefixo}/logs/zip?servico=&amp;de=&amp;ate=&amp;arquivos=Servico/arquivo.log (vários num .zip). Só Admin.
    /// </summary>
    public static RouteGroupBuilder MapLogFileEndpoints(this IEndpointRouteBuilder app, string prefixo)
    {
        var group = app.MapGroup($"{prefixo.TrimEnd('/')}/logs").RequireAuthorization(Policies.Admin).WithTags("Logs");

        group.MapGet("/", (string? servico, DateOnly? de, DateOnly? ate, IConfiguration configuration, IHostEnvironment environment) =>
        {
            var pastas = Pastas(configuration, environment);
            return Results.Ok(new LogFilesResponse(
                pastas.Select(p => new LogFolderInfo(p.Key, Directory.Exists(p.Value))).ToList(),
                Listar(pastas, servico, de, ate)));
        });

        group.MapGet("/zip", (string? servico, DateOnly? de, DateOnly? ate, string[]? arquivos, IConfiguration configuration, IHostEnvironment environment) =>
        {
            var pastas = Pastas(configuration, environment);
            var itens = new List<(string Servico, string Caminho, string Nome)>();
            if (arquivos is { Length: > 0 })
            {
                // Cada item no formato "Servico/arquivo.log"
                foreach (var item in arquivos.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var partes = item.Split('/', 2);
                    var caminho = partes.Length == 2 ? Resolver(pastas, partes[0], partes[1]) : null;
                    if (caminho is null) return ServiceDefaults.ValidationError("arquivos", $"Arquivo de log inválido ou inexistente: {item}.");
                    itens.Add((pastas.Keys.First(k => string.Equals(k, partes[0], StringComparison.OrdinalIgnoreCase)), caminho, partes[1]));
                }
            }
            else
            {
                foreach (var f in Listar(pastas, servico, de, ate))
                    if (Resolver(pastas, f.Servico, f.Arquivo) is { } caminho) itens.Add((f.Servico, caminho, f.Arquivo));
            }
            if (itens.Count == 0) return Results.Problem(statusCode: StatusCodes.Status404NotFound, detail: "Nenhum arquivo de log no filtro informado.");
            if (itens.Count > MaxArquivosZip)
                return ServiceDefaults.ValidationError("arquivos", $"São {itens.Count} arquivos; o zip aceita até {MaxArquivosZip}. Filtre por serviço ou período.");

            var periodo = de is null && ate is null ? DateTime.Now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture)
                : $"{de?.ToString("yyyyMMdd", CultureInfo.InvariantCulture) ?? "inicio"}-{ate?.ToString("yyyyMMdd", CultureInfo.InvariantCulture) ?? "hoje"}";
            var nomeServico = pastas.Keys.FirstOrDefault(k => string.Equals(k, servico, StringComparison.OrdinalIgnoreCase)) ?? environment.ApplicationName;
            return new ZipDeLogs(itens, $"logs-{nomeServico}-{periodo}.zip");
        });

        group.MapGet("/{servico}/{arquivo}", (string servico, string arquivo, IConfiguration configuration, IHostEnvironment environment) =>
        {
            var caminho = Resolver(Pastas(configuration, environment), servico, arquivo);
            if (caminho is null) return Results.NotFound();
            FileStream stream;
            try
            {
                stream = Abrir(caminho);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Apagado pela retenção entre a listagem e o download, ou sem permissão de leitura
                return Results.NotFound();
            }
            return new ArquivoDeLog(stream, arquivo);
        });

        return group;
    }

    /// <summary>Um arquivo em streaming (texto), até o tamanho que tinha ao abrir.</summary>
    private sealed class ArquivoDeLog(FileStream stream, string nome) : IResult
    {
        public async Task ExecuteAsync(HttpContext http)
        {
            await using (stream)
            {
                var tamanho = stream.Length;
                http.Response.ContentType = "text/plain; charset=utf-8";
                http.Response.ContentLength = tamanho;
                http.Response.Headers.ContentDisposition = Anexo(nome);
                await CopiarAsync(stream, http.Response.Body, tamanho, http.RequestAborted);
            }
        }
    }

    /// <summary>Vários arquivos num .zip gerado direto na resposta (sem arquivo temporário), um diretório por serviço.</summary>
    private sealed class ZipDeLogs(IReadOnlyList<(string Servico, string Caminho, string Nome)> itens, string nome) : IResult
    {
        public async Task ExecuteAsync(HttpContext http)
        {
            var ct = http.RequestAborted;
            http.Response.ContentType = "application/zip";
            http.Response.Headers.ContentDisposition = Anexo(nome);
            await using var zip = await ZipArchive.CreateAsync(http.Response.Body, ZipArchiveMode.Create, leaveOpen: true, entryNameEncoding: null, ct);
            foreach (var (servico, caminho, arquivo) in itens)
            {
                FileStream origem;
                try
                {
                    origem = Abrir(caminho);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue; // apagado pela retenção no meio do caminho: segue com os demais
                }
                await using (origem)
                {
                    var entrada = zip.CreateEntry($"{servico}/{arquivo}", CompressionLevel.Optimal);
                    entrada.LastWriteTime = File.GetLastWriteTime(caminho);
                    await using var destino = await entrada.OpenAsync(ct);
                    await CopiarAsync(origem, destino, origem.Length, ct);
                }
            }
        }
    }
}

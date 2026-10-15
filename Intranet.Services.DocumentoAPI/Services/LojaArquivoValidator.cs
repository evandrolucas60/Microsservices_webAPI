using Intranet.Services.DocumentoAPI.Models;

namespace Intranet.Services.DocumentoAPI.Services;

/// <summary>
/// Validação dos arquivos das seções das lojas. O tipo é identificado pelo conteúdo do
/// arquivo (assinatura/"magic bytes"); a extensão só define qual documento é esperado.
/// </summary>
public static class LojaArquivoValidator
{
    public const long ImagemMaxBytes = 10 * 1024 * 1024;
    public const long DocumentoMaxBytes = 20 * 1024 * 1024;
    public const int MaxArquivosPorEnvio = 20;

    /// <summary>Limite do corpo da requisição de envio (vários arquivos + campos do formulário).</summary>
    public const long MaxBytesPorEnvio = 200L * 1024 * 1024;

    // Documentos aceitos: extensão -> (Content-Type, assinatura exigida)
    private static readonly Dictionary<string, (string ContentType, Func<byte[], bool> Confere)> Documentos =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [".pdf"] = ("application/pdf", d => ComecaCom(d, "%PDF-"u8)),
            [".docx"] = ("application/vnd.openxmlformats-officedocument.wordprocessingml.document", EhZip),
            [".xlsx"] = ("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", EhZip),
            [".pptx"] = ("application/vnd.openxmlformats-officedocument.presentationml.presentation", EhZip),
            [".odt"] = ("application/vnd.oasis.opendocument.text", EhZip),
            [".ods"] = ("application/vnd.oasis.opendocument.spreadsheet", EhZip),
            [".doc"] = ("application/msword", EhOle),
            [".xls"] = ("application/vnd.ms-excel", EhOle),
            [".ppt"] = ("application/vnd.ms-powerpoint", EhOle),
            [".txt"] = ("text/plain; charset=utf-8", EhTexto),
            [".csv"] = ("text/csv; charset=utf-8", EhTexto),
        };

    public static string DocumentosAceitos => string.Join(", ", Documentos.Keys);

    private static bool ComecaCom(byte[] dados, ReadOnlySpan<byte> prefixo) =>
        dados.Length >= prefixo.Length && dados.AsSpan(0, prefixo.Length).SequenceEqual(prefixo);

    private static bool EhZip(byte[] d) => ComecaCom(d, new byte[] { 0x50, 0x4B, 0x03, 0x04 });
    private static bool EhOle(byte[] d) => ComecaCom(d, new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 });
    private static bool EhTexto(byte[] d) => Array.IndexOf(d, (byte)0) < 0;

    /// <summary>Tipo MIME real da imagem (JPEG, PNG, GIF ou WebP), ou null.</summary>
    public static string? DetectarImagem(ReadOnlySpan<byte> d)
    {
        if (d.Length >= 3 && d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF) return "image/jpeg";
        if (d.Length >= 8 && d[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) return "image/png";
        if (d.Length >= 6 && (d[..6].SequenceEqual("GIF87a"u8) || d[..6].SequenceEqual("GIF89a"u8))) return "image/gif";
        if (d.Length >= 12 && d[..4].SequenceEqual("RIFF"u8) && d[8..12].SequenceEqual("WEBP"u8)) return "image/webp";
        return null;
    }

    /// <summary>
    /// Nome seguro para gravar e para o Content-Disposition: sem pastas, sem caracteres de
    /// controle ou inválidos, com no máximo 255 caracteres (preservando a extensão).
    /// </summary>
    public static string SanitizarNome(string? nomeOriginal, string padrao = "arquivo")
    {
        var nome = (nomeOriginal ?? string.Empty).Replace('\\', '/');
        nome = nome[(nome.LastIndexOf('/') + 1)..];

        var invalidos = Path.GetInvalidFileNameChars().Concat(new[] { '"', '<', '>', '|', ':', '*', '?' }).ToHashSet();
        nome = new string(nome.Where(c => !char.IsControl(c) && !invalidos.Contains(c)).ToArray()).Trim().Trim('.').Trim();
        if (string.IsNullOrWhiteSpace(nome)) nome = padrao;

        if (nome.Length > LojaArquivo.NomeArquivoMaxLength)
        {
            var extensao = Path.GetExtension(nome);
            if (extensao.Length > 20) extensao = string.Empty;
            nome = nome[..(LojaArquivo.NomeArquivoMaxLength - extensao.Length)] + extensao;
        }
        return nome;
    }

    /// <summary>Resultado da leitura/validação de um arquivo enviado.</summary>
    public record Resultado(byte[]? Conteudo, string? Tipo, string? ContentType, string NomeArquivo, string? Erro);

    /// <summary>Lê e classifica o arquivo (imagem ou documento). Retorna o conteúdo ou a mensagem de erro.</summary>
    public static async Task<Resultado> LerAsync(IFormFile? arquivo, CancellationToken ct)
    {
        if (arquivo is null) return new(null, null, null, string.Empty, "Selecione um arquivo.");

        var nome = SanitizarNome(arquivo.FileName);
        if (arquivo.Length == 0) return new(null, null, null, nome, $"O arquivo {nome} está vazio.");
        if (arquivo.Length > DocumentoMaxBytes)
            return new(null, null, null, nome, $"O arquivo {nome} excede o limite de 20 MB.");

        byte[] conteudo;
        using (var buffer = new MemoryStream((int)arquivo.Length))
        {
            await arquivo.CopyToAsync(buffer, ct);
            conteudo = buffer.ToArray();
        }

        var imagem = DetectarImagem(conteudo);
        if (imagem is not null)
        {
            return conteudo.Length <= ImagemMaxBytes
                ? new(conteudo, LojaArquivoTipos.Imagem, imagem, nome, null)
                : new(null, null, null, nome, $"A imagem {nome} excede o limite de 10 MB.");
        }

        var extensao = Path.GetExtension(nome);
        if (Documentos.TryGetValue(extensao, out var doc))
        {
            return doc.Confere(conteudo)
                ? new(conteudo, LojaArquivoTipos.Documento, doc.ContentType, nome, null)
                : new(null, null, null, nome, $"O conteúdo de {nome} não corresponde a um arquivo {extensao.ToLowerInvariant()}.");
        }

        return new(null, null, null, nome,
            $"Tipo de arquivo não permitido: {nome}. Envie imagens JPG, PNG, GIF ou WebP (até 10 MB) " +
            $"ou documentos ({DocumentosAceitos}) de até 20 MB.");
    }
}

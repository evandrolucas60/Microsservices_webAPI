namespace Intranet.Services.ConteudoAPI.Services;

/// <summary>
/// Validação de imagens enviadas pelo usuário: aceita apenas JPEG, PNG, GIF e WebP,
/// identificados pelo conteúdo do arquivo (não pela extensão nem pelo Content-Type do cliente).
/// </summary>
public static class ImageFiles
{
    public const long MaxBytes = 5 * 1024 * 1024;

    /// <summary>Tipo MIME real da imagem, ou null se o conteúdo não for de um formato aceito.</summary>
    public static string? DetectContentType(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF) return "image/jpeg";
        if (data.Length >= 8 && data[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) return "image/png";
        if (data.Length >= 6 && (data[..6].SequenceEqual("GIF87a"u8) || data[..6].SequenceEqual("GIF89a"u8))) return "image/gif";
        if (data.Length >= 12 && data[..4].SequenceEqual("RIFF"u8) && data[8..12].SequenceEqual("WEBP"u8)) return "image/webp";
        return null;
    }

    /// <summary>Lê e valida o arquivo. Retorna (conteúdo, tipo, nome) ou a mensagem de erro.</summary>
    public static async Task<(byte[]? Content, string? ContentType, string FileName, string? Error)> ReadAsync(IFormFile? file, CancellationToken ct)
    {
        if (file is null || file.Length == 0) return (null, null, string.Empty, "Selecione uma imagem.");
        if (file.Length > MaxBytes) return (null, null, string.Empty, "A imagem deve ter no máximo 5 MB.");

        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);
        var content = buffer.ToArray();

        var contentType = DetectContentType(content);
        if (contentType is null) return (null, null, string.Empty, "Formato não suportado. Envie uma imagem JPG, PNG, GIF ou WebP.");

        var fileName = Path.GetFileName(file.FileName);
        if (string.IsNullOrWhiteSpace(fileName)) fileName = "imagem";
        if (fileName.Length > 255) fileName = fileName[^255..];

        return (content, contentType, fileName, null);
    }
}

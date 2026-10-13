namespace Intranet.Services.ComunidadesAPI.Services;

/// <summary>Imagem validada, pronta para gravar no banco.</summary>
public record ValidatedImage(byte[] Data, string ContentType);

/// <summary>
/// Validação de imagens enviadas (capa e publicações): apenas JPG, PNG, GIF e WEBP,
/// até 5 MB, conferindo a assinatura do arquivo (magic bytes) e não só a extensão.
/// </summary>
public static class ImageValidator
{
    public const long MaxBytes = 5 * 1024 * 1024;

    /// <summary>Limite do corpo multipart (imagem + campos de texto).</summary>
    public const long MaxRequestBytes = MaxBytes + 512 * 1024;

    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp",
    };

    /// <summary>Lê e valida o arquivo. Retorna a imagem ou a mensagem de erro.</summary>
    public static async Task<(ValidatedImage? Image, string? Error)> ReadAsync(IFormFile? file, CancellationToken ct)
    {
        if (file is null || file.Length == 0) return (null, "Selecione uma imagem.");
        if (file.Length > MaxBytes) return (null, "A imagem deve ter no máximo 5 MB.");

        var extension = Path.GetExtension(file.FileName ?? string.Empty);
        if (!string.IsNullOrEmpty(extension) && !AllowedExtensions.Contains(extension))
            return (null, "Formato não permitido. Envie uma imagem JPG, PNG, GIF ou WEBP.");

        byte[] data;
        using (var ms = new MemoryStream((int)file.Length))
        {
            await using var stream = file.OpenReadStream();
            await stream.CopyToAsync(ms, ct);
            data = ms.ToArray();
        }
        if (data.Length == 0) return (null, "Selecione uma imagem.");
        if (data.Length > MaxBytes) return (null, "A imagem deve ter no máximo 5 MB.");

        var contentType = DetectContentType(data);
        if (contentType is null)
            return (null, "O arquivo não é uma imagem válida. Envie uma imagem JPG, PNG, GIF ou WEBP.");

        return (new ValidatedImage(data, contentType), null);
    }

    /// <summary>Tipo da imagem pela assinatura do arquivo, ou null se não for um formato aceito.</summary>
    public static string? DetectContentType(ReadOnlySpan<byte> data)
    {
        // JPEG: FF D8 FF
        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
            return "image/jpeg";

        // PNG: 89 50 4E 47 0D 0A 1A 0A
        if (data.Length >= 8 && data[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
            return "image/png";

        // GIF: "GIF87a" ou "GIF89a"
        if (data.Length >= 6 && data[..3].SequenceEqual("GIF"u8) && (data.Slice(3, 3).SequenceEqual("87a"u8) || data.Slice(3, 3).SequenceEqual("89a"u8)))
            return "image/gif";

        // WEBP: "RIFF" .... "WEBP"
        if (data.Length >= 12 && data[..4].SequenceEqual("RIFF"u8) && data.Slice(8, 4).SequenceEqual("WEBP"u8))
            return "image/webp";

        return null;
    }
}

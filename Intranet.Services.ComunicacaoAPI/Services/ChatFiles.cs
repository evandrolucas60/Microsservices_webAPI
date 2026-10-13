namespace Intranet.Services.ComunicacaoAPI.Services;

/// <summary>Tipos de anexo do chat.</summary>
public static class ChatAttachmentKinds
{
    public const string Image = "image";
    public const string Audio = "audio";
    public const string Document = "document";
}

/// <summary>
/// Validação dos arquivos enviados no chat (imagem, áudio gravado e documentos).
/// O tipo é identificado pelo conteúdo do arquivo; a extensão só desempata documentos.
/// </summary>
public static class ChatFiles
{
    public const long MaxBytes = 10 * 1024 * 1024;

    // Documentos aceitos: extensão -> (Content-Type, assinatura exigida)
    private static readonly Dictionary<string, (string ContentType, Func<byte[], bool> Matches)> Documents =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [".pdf"] = ("application/pdf", d => StartsWith(d, "%PDF-"u8)),
            [".docx"] = ("application/vnd.openxmlformats-officedocument.wordprocessingml.document", IsZip),
            [".xlsx"] = ("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", IsZip),
            [".pptx"] = ("application/vnd.openxmlformats-officedocument.presentationml.presentation", IsZip),
            [".odt"] = ("application/vnd.oasis.opendocument.text", IsZip),
            [".ods"] = ("application/vnd.oasis.opendocument.spreadsheet", IsZip),
            [".doc"] = ("application/msword", IsOleCompound),
            [".xls"] = ("application/vnd.ms-excel", IsOleCompound),
            [".ppt"] = ("application/vnd.ms-powerpoint", IsOleCompound),
            [".txt"] = ("text/plain; charset=utf-8", IsPlainText),
            [".csv"] = ("text/csv; charset=utf-8", IsPlainText),
        };

    public static string AcceptedDocuments => string.Join(", ", Documents.Keys);

    private static bool StartsWith(byte[] data, ReadOnlySpan<byte> prefix) =>
        data.Length >= prefix.Length && data.AsSpan(0, prefix.Length).SequenceEqual(prefix);

    private static bool IsZip(byte[] d) => StartsWith(d, new byte[] { 0x50, 0x4B, 0x03, 0x04 });
    private static bool IsOleCompound(byte[] d) => StartsWith(d, new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 });
    private static bool IsPlainText(byte[] d) => Array.IndexOf(d, (byte)0) < 0;

    private static string? DetectAudio(byte[] d)
    {
        if (StartsWith(d, new byte[] { 0x1A, 0x45, 0xDF, 0xA3 })) return "audio/webm";      // MediaRecorder (Chrome/Edge)
        if (StartsWith(d, "OggS"u8)) return "audio/ogg";                                      // MediaRecorder (Firefox)
        if (d.Length >= 12 && d.AsSpan(4, 4).SequenceEqual("ftyp"u8)) return "audio/mp4";    // MediaRecorder (Safari) / .m4a
        if (StartsWith(d, "ID3"u8) || (d.Length >= 2 && d[0] == 0xFF && (d[1] & 0xE0) == 0xE0)) return "audio/mpeg";
        if (d.Length >= 12 && StartsWith(d, "RIFF"u8) && d.AsSpan(8, 4).SequenceEqual("WAVE"u8)) return "audio/wav";
        return null;
    }

    /// <summary>
    /// Lê e classifica o arquivo. kindHint "audio" indica gravação do microfone.
    /// Retorna (conteúdo, tipo de anexo, Content-Type, nome) ou a mensagem de erro.
    /// </summary>
    public static async Task<(byte[]? Content, string? Kind, string? ContentType, string FileName, string? Error)> ReadAsync(
        IFormFile? file, string? kindHint, CancellationToken ct)
    {
        if (file is null || file.Length == 0) return (null, null, null, string.Empty, "Selecione um arquivo.");
        if (file.Length > MaxBytes) return (null, null, null, string.Empty, "O arquivo deve ter no máximo 10 MB.");

        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);
        var content = buffer.ToArray();

        var fileName = Path.GetFileName(file.FileName);
        if (string.IsNullOrWhiteSpace(fileName)) fileName = "arquivo";
        if (fileName.Length > 255) fileName = fileName[^255..];
        var extension = Path.GetExtension(fileName);

        var image = ImageFiles.DetectContentType(content);
        if (image is not null && content.Length <= ImageFiles.MaxBytes)
            return (content, ChatAttachmentKinds.Image, image, fileName, null);

        // Documentos pela extensão + assinatura (um .docx também é um zip, por isso vem antes do áudio)
        if (Documents.TryGetValue(extension, out var doc))
        {
            return doc.Matches(content)
                ? (content, ChatAttachmentKinds.Document, doc.ContentType, fileName, null)
                : (null, null, null, fileName, $"O conteúdo de {fileName} não corresponde a um arquivo {extension}.");
        }

        var audio = DetectAudio(content);
        if (audio is not null && (kindHint == ChatAttachmentKinds.Audio || extension is ".webm" or ".ogg" or ".oga" or ".m4a" or ".mp3" or ".wav"))
            return (content, ChatAttachmentKinds.Audio, audio, fileName, null);

        return (null, null, null, fileName,
            $"Tipo de arquivo não permitido. Envie imagem (até 5 MB), áudio ou documento ({AcceptedDocuments}).");
    }
}

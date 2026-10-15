using System.Text;
using BuildingBlocks.Contracts.Administracao;
using BuildingBlocks.Shared.Security;
using Intranet.Services.IdentidadeAPI.Data;
using Intranet.Services.IdentidadeAPI.Models;
using Intranet.Services.IdentidadeAPI.Models.DTOs;
using Intranet.Services.IdentidadeAPI.Utility;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;

namespace Intranet.Services.IdentidadeAPI.Controllers;

/// <summary>
/// Certificados (PDF) dos colaboradores. Qualquer usuário autenticado vê os certificados
/// de qualquer membro; enviar e excluir só o próprio dono (membro do claim "id" do token).
/// </summary>
[Route("api/[controller]")]
[ApiController]
[Authorize]
public class CertificadosController : ControllerBase
{
    private const long MaxFileBytes = 10 * 1024 * 1024;
    // Folga para os demais campos do formulário multipart
    private const long MaxRequestBytes = MaxFileBytes + 1024 * 1024;
    private static readonly byte[] PdfMagic = Encoding.ASCII.GetBytes("%PDF-");

    private readonly AppDbContext _db;
    private readonly IOwnershipChecker _ownership;
    private readonly ILogger<CertificadosController> _logger;

    public CertificadosController(AppDbContext db, IOwnershipChecker ownership, ILogger<CertificadosController> logger)
    {
        _db = db;
        _ownership = ownership;
        _logger = logger;
    }

    // Projeção sem o conteúdo (bytea): listas nunca carregam o arquivo
    private static IQueryable<CertificateDto> SemConteudo(IQueryable<Certificate> query) =>
        query.Select(c => new CertificateDto
        {
            CertificateId = c.CertificateId,
            MemberId = c.MemberId,
            Title = c.Title,
            Issuer = c.Issuer,
            IssueDate = c.IssueDate,
            FileName = c.FileName,
            FileSize = c.FileSize,
            CreatedAt = c.CreatedAt,
        });

    private async Task<Guid?> MemberIdDoTokenAsync()
    {
        var userId = _ownership.UserId(User);
        if (string.IsNullOrEmpty(userId)) return null;

        return await _db.Members.AsNoTracking()
            .Where(m => m.UserId == userId)
            .Select(m => (Guid?)m.MemberId)
            .FirstOrDefaultAsync();
    }

    private static ResponseDto Erro(string message) => new() { IsSuccess = false, Message = message };

    // Nome seguro para o header Content-Disposition: sem caminho/caracteres de controle e sempre .pdf
    private static string NomeArquivoSeguro(string? original)
    {
        var nome = Path.GetFileName(original ?? string.Empty);
        var invalidos = Path.GetInvalidFileNameChars();
        nome = new string(nome.Where(ch => !char.IsControl(ch) && !invalidos.Contains(ch) && ch != '"').ToArray()).Trim();

        if (string.IsNullOrWhiteSpace(Path.GetFileNameWithoutExtension(nome))) nome = "certificado.pdf";
        if (!nome.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) nome = Path.GetFileNameWithoutExtension(nome) + ".pdf";

        if (nome.Length > Certificate.FileNameMaxLength)
        {
            nome = nome[..(Certificate.FileNameMaxLength - 4)] + ".pdf";
        }
        return nome;
    }

    /// <summary>Certificados de um membro (metadados), do mais recente para o mais antigo.</summary>
    [AllowAnonymous]
    [HttpGet("membro/{memberId:guid}")]
    public async Task<ActionResult> ListarPorMembro(Guid memberId)
    {
        var result = await SemConteudo(_db.Certificates.AsNoTracking().Where(c => c.MemberId == memberId))
            .OrderByDescending(c => c.IssueDate.HasValue)
            .ThenByDescending(c => c.IssueDate)
            .ThenByDescending(c => c.CreatedAt)
            .ToListAsync();

        return Ok(new ResponseDto { Result = result });
    }

    /// <summary>Certificados do usuário autenticado.</summary>
    [HttpGet("me")]
    public async Task<ActionResult> MeusCertificados()
    {
        var memberId = await MemberIdDoTokenAsync();
        if (memberId is null) return NotFound(Erro("Membro não encontrado."));
        return await ListarPorMembro(memberId.Value);
    }

    /// <summary>Arquivo PDF do certificado (exibido inline pelo navegador).</summary>
    [HttpGet("{certificateId:guid}/arquivo")]
    public async Task<ActionResult> Arquivo(Guid certificateId)
    {
        var arquivo = await _db.Certificates.AsNoTracking()
            .Where(c => c.CertificateId == certificateId)
            .Select(c => new { c.FileName, c.Content })
            .FirstOrDefaultAsync();

        if (arquivo is null) return NotFound(Erro("Certificado não encontrado."));

        var nome = NomeArquivoSeguro(arquivo.FileName);
        var disposition = new ContentDispositionHeaderValue("inline");
        // Preenche filename (com escape/aspas) e filename* (UTF-8) para nomes com acentos
        disposition.SetHttpFileName(nome);

        Response.Headers[HeaderNames.ContentDisposition] = disposition.ToString();
        Response.Headers[HeaderNames.XContentTypeOptions] = "nosniff";
        Response.Headers[HeaderNames.CacheControl] = "private, no-store";

        return File(arquivo.Content, "application/pdf");
    }

    /// <summary>
    /// Envia um certificado para o usuário autenticado (multipart/form-data:
    /// title, issuer, issueDate, file). Apenas PDF (verificado pelo conteúdo), até 10 MB.
    /// </summary>
    [HttpPost]
    [RequerServico(ServicosRestringiveis.Arquivos)]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaxRequestBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxRequestBytes)]
    public async Task<ActionResult> Enviar([FromForm] CertificateUploadDto dto)
    {
        var memberId = await MemberIdDoTokenAsync();
        if (memberId is null) return NotFound(Erro("Membro não encontrado. Abra o seu perfil antes de enviar certificados."));

        var titulo = dto?.Title?.Trim();
        if (string.IsNullOrEmpty(titulo)) return BadRequest(Erro("Informe o título do certificado."));
        if (titulo.Length > Certificate.TitleMaxLength) return BadRequest(Erro($"O título deve ter no máximo {Certificate.TitleMaxLength} caracteres."));

        var emissor = dto!.Issuer?.Trim();
        if (string.IsNullOrEmpty(emissor)) emissor = null;
        if (emissor is { Length: > Certificate.IssuerMaxLength }) return BadRequest(Erro($"O emissor deve ter no máximo {Certificate.IssuerMaxLength} caracteres."));

        if (dto.IssueDate is { } data &&
            (data.Year < 1900 || data > DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1)))
        {
            return BadRequest(Erro("Data de emissão inválida."));
        }

        var file = dto.File;
        if (file is null || file.Length == 0) return BadRequest(Erro("Selecione o arquivo PDF do certificado."));
        if (file.Length > MaxFileBytes) return BadRequest(Erro("O arquivo deve ter no máximo 10 MB."));

        byte[] conteudo;
        using (var ms = new MemoryStream((int)file.Length))
        {
            await file.CopyToAsync(ms);
            conteudo = ms.ToArray();
        }

        // Valida pelo conteúdo (assinatura %PDF-), não pela extensão/Content-Type informados pelo cliente
        if (conteudo.Length < PdfMagic.Length || !conteudo.AsSpan(0, PdfMagic.Length).SequenceEqual(PdfMagic))
        {
            return BadRequest(Erro("O arquivo enviado não é um PDF válido."));
        }

        var certificado = new Certificate
        {
            CertificateId = Guid.NewGuid(),
            MemberId = memberId.Value,
            Title = titulo,
            Issuer = emissor,
            IssueDate = dto.IssueDate,
            FileName = NomeArquivoSeguro(file.FileName),
            FileSize = conteudo.LongLength,
            Content = conteudo,
            CreatedAt = DateTime.UtcNow,
        };

        try
        {
            _db.Certificates.Add(certificado);
            await _db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao salvar certificado do membro {MemberId}", memberId);
            return StatusCode(500, Erro("Não foi possível salvar o certificado."));
        }

        var result = new CertificateDto
        {
            CertificateId = certificado.CertificateId,
            MemberId = certificado.MemberId,
            Title = certificado.Title,
            Issuer = certificado.Issuer,
            IssueDate = certificado.IssueDate,
            FileName = certificado.FileName,
            FileSize = certificado.FileSize,
            CreatedAt = certificado.CreatedAt,
        };

        return CreatedAtAction(nameof(Arquivo), new { certificateId = certificado.CertificateId },
            new ResponseDto { Result = result, Message = "Certificado enviado." });
    }

    /// <summary>Exclui um certificado: apenas o dono (ou Admin).</summary>
    [HttpDelete("{certificateId:guid}")]
    public async Task<ActionResult> Excluir(Guid certificateId)
    {
        var dono = await _db.Certificates.AsNoTracking()
            .Where(c => c.CertificateId == certificateId)
            .Select(c => (Guid?)c.MemberId)
            .FirstOrDefaultAsync();

        if (dono is null) return NotFound(Erro("Certificado não encontrado."));

        if (!_ownership.IsAdmin(User))
        {
            var memberId = await MemberIdDoTokenAsync();
            if (memberId != dono) return StatusCode(StatusCodes.Status403Forbidden, Erro("Você só pode excluir os seus certificados."));
        }

        // Exclui sem carregar o conteúdo do arquivo
        await _db.Certificates.Where(c => c.CertificateId == certificateId).ExecuteDeleteAsync();

        return Ok(new ResponseDto { Message = "Certificado excluído." });
    }
}

using System.Linq.Expressions;
using BuildingBlocks.Contracts.Administracao;
using BuildingBlocks.Shared.Security;
using Intranet.Services.DocumentoAPI.Data;
using Intranet.Services.DocumentoAPI.Models;
using Intranet.Services.DocumentoAPI.Models.DTOs;
using Intranet.Services.DocumentoAPI.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;
using Npgsql;

namespace Intranet.Services.DocumentoAPI.Controllers;

/// <summary>
/// Lojas do GC (Gerenciamento por Categoria) com seções fixas e seus arquivos (fotos e documentos).
/// A consulta (lojas, seções e arquivos) é pública, inclusive para usuários anônimos — somente leitura;
/// criar/alterar/remover exige login e um dos papéis de gerenciamento
/// (padrão: superAdmin ou GC — veja <see cref="LojaPermissoes"/>).
/// </summary>
[Route("api/lojas")]
[ApiController]
[Authorize]
public class LojasController : ControllerBase
{
    private const string SemPermissao = "Apenas usuários com perfil superAdmin ou GC podem criar, alterar ou remover lojas e arquivos.";
    private const string LojaNaoEncontrada = "Loja não encontrada.";

    private readonly AppDbContext _db;
    private readonly LojaPermissoes _permissoes;

    public LojasController(AppDbContext db, LojaPermissoes permissoes)
    {
        _db = db;
        _permissoes = permissoes;
    }

    private static ResponseDto Falha(string message) => new() { IsSuccess = false, Message = message };

    private string? UsuarioId => User.FindFirst("id")?.Value;

    private bool PodeGerenciar => _permissoes.PodeGerenciar(User);

    private ObjectResult Proibido() => StatusCode(StatusCodes.Status403Forbidden, Falha(SemPermissao));

    /// <summary>Projeção sem o conteúdo binário (nunca carregar bytes em listagens).</summary>
    private static readonly Expression<Func<LojaArquivo, LojaArquivoDto>> ComoDto = a => new LojaArquivoDto
    {
        Id = a.LojaArquivoId,
        LojaId = a.LojaId,
        Secao = a.Secao,
        Tipo = a.Tipo,
        NomeArquivo = a.NomeArquivo,
        ContentType = a.ContentType,
        Tamanho = a.Tamanho,
        Descricao = a.Descricao,
        EnviadoPor = a.EnviadoPor,
        CriadoEm = a.CriadoEm,
    };

    /// <summary>Valida e limpa o nome da loja. Retorna (nome, erro).</summary>
    private static (string? Nome, string? Erro) ValidarNome(string? nome)
    {
        var valor = nome?.Trim();
        if (string.IsNullOrWhiteSpace(valor)) return (null, "Informe o nome da loja.");
        if (valor.Length > Loja.NomeMaxLength) return (null, $"O nome da loja deve ter no máximo {Loja.NomeMaxLength} caracteres.");
        if (valor.Any(char.IsControl)) return (null, "O nome da loja contém caracteres inválidos.");
        return (valor, null);
    }

    private static bool EhNomeDuplicado(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    private static string NomeJaExiste(string nome) => $"Já existe uma loja chamada \"{nome}\".";

    // ---------------------------------------------------------------- consulta (pública, somente leitura)

    /// <summary>
    /// Permissões do usuário (a interface mostra ou esconde os controles de gerenciamento).
    /// Sem login devolve podeGerenciar = false.
    /// </summary>
    [HttpGet("permissoes")]
    [AllowAnonymous]
    public ActionResult Permissoes() => Ok(new ResponseDto { Result = new { podeGerenciar = PodeGerenciar } });

    /// <summary>Seções fixas de toda loja, na ordem oficial.</summary>
    [HttpGet("secoes")]
    [AllowAnonymous]
    public ActionResult Secoes() => Ok(new ResponseDto { Result = LojaSecoes.Todas });

    /// <summary>Lojas (busca opcional pelo nome) com a quantidade de arquivos por seção e o total.</summary>
    [HttpGet]
    [AllowAnonymous]
    public async Task<ActionResult> Listar([FromQuery] string? busca, CancellationToken ct)
    {
        var query = _db.lojas.AsNoTracking();
        var termo = busca?.Trim();
        if (!string.IsNullOrEmpty(termo))
        {
            var normalizado = Loja.Normalizar(termo);
            query = query.Where(l => l.NomeNormalizado.Contains(normalizado));
        }

        var lojas = await query
            .OrderBy(l => l.Nome)
            .Select(l => new { l.LojaId, l.Nome, l.CriadoEm, l.AtualizadoEm })
            .ToListAsync(ct);

        var ids = lojas.Select(l => l.LojaId).ToList();
        var contagens = await _db.lojaArquivos.AsNoTracking()
            .Where(a => ids.Contains(a.LojaId))
            .GroupBy(a => new { a.LojaId, a.Secao })
            .Select(g => new { g.Key.LojaId, g.Key.Secao, Total = g.Count() })
            .ToListAsync(ct);

        var resultado = lojas.Select(l =>
        {
            var porSecao = LojaSecoes.Todas.ToDictionary(s => s, _ => 0);
            foreach (var c in contagens.Where(c => c.LojaId == l.LojaId))
            {
                porSecao[c.Secao] = porSecao.GetValueOrDefault(c.Secao) + c.Total;
            }
            return new LojaResumoDto
            {
                Id = l.LojaId,
                Nome = l.Nome,
                CriadoEm = l.CriadoEm,
                AtualizadoEm = l.AtualizadoEm,
                ArquivosPorSecao = porSecao,
                TotalArquivos = porSecao.Values.Sum(),
            };
        }).ToList();

        return Ok(new ResponseDto { Result = resultado });
    }

    /// <summary>Loja com todas as seções fixas e os metadados dos arquivos de cada uma.</summary>
    [HttpGet("{id:int}")]
    [AllowAnonymous]
    public async Task<ActionResult> Detalhe(int id, CancellationToken ct)
    {
        var loja = await _db.lojas.AsNoTracking()
            .Where(l => l.LojaId == id)
            .Select(l => new { l.LojaId, l.Nome, l.CriadoEm, l.AtualizadoEm, l.CriadoPor })
            .FirstOrDefaultAsync(ct);
        if (loja is null) return NotFound(Falha(LojaNaoEncontrada));

        var arquivos = await _db.lojaArquivos.AsNoTracking()
            .Where(a => a.LojaId == id)
            .OrderByDescending(a => a.CriadoEm).ThenByDescending(a => a.LojaArquivoId)
            .Select(ComoDto)
            .ToListAsync(ct);

        var secoes = LojaSecoes.Todas.Select(s =>
        {
            var daSecao = arquivos.Where(a => a.Secao == s).ToList();
            return new LojaSecaoDto { Nome = s, TotalArquivos = daSecao.Count, Arquivos = daSecao };
        }).ToList();

        return Ok(new ResponseDto
        {
            Result = new LojaDetalheDto
            {
                Id = loja.LojaId,
                Nome = loja.Nome,
                CriadoEm = loja.CriadoEm,
                AtualizadoEm = loja.AtualizadoEm,
                CriadoPor = loja.CriadoPor,
                TotalArquivos = arquivos.Count,
                Secoes = secoes,
            }
        });
    }

    /// <summary>
    /// Conteúdo do arquivo: imagem exibida no navegador, documento sempre como download.
    /// Público como o restante da consulta: os documentos são da mesma loja/seção que as imagens.
    /// </summary>
    [HttpGet("arquivos/{arquivoId:int}")]
    [AllowAnonymous]
    public async Task<ActionResult> Baixar(int arquivoId, CancellationToken ct)
    {
        var arquivo = await _db.lojaArquivos.AsNoTracking()
            .Where(a => a.LojaArquivoId == arquivoId)
            .Select(a => new { a.Conteudo, a.ContentType, a.NomeArquivo, a.Tipo })
            .FirstOrDefaultAsync(ct);
        if (arquivo is null) return NotFound(Falha("Arquivo não encontrado."));

        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers.CacheControl = "private, max-age=86400";

        if (arquivo.Tipo == LojaArquivoTipos.Imagem)
        {
            var inline = new ContentDispositionHeaderValue("inline");
            inline.SetHttpFileName(arquivo.NomeArquivo);
            Response.Headers.ContentDisposition = inline.ToString();
            return File(arquivo.Conteudo, arquivo.ContentType);
        }

        // Documentos: "attachment" com o nome do arquivo (nunca renderizados pelo navegador)
        return File(arquivo.Conteudo, arquivo.ContentType, arquivo.NomeArquivo);
    }

    // ---------------------------------------------------------------- gerenciamento

    /// <summary>Cria uma loja (nome único, sem diferenciar maiúsculas de minúsculas).</summary>
    [HttpPost]
    public async Task<ActionResult> Criar([FromBody] LojaNomeDto dto, CancellationToken ct)
    {
        if (!PodeGerenciar) return Proibido();

        var (nome, erro) = ValidarNome(dto?.Nome);
        if (erro is not null) return BadRequest(Falha(erro));

        var normalizado = Loja.Normalizar(nome!);
        if (await _db.lojas.AnyAsync(l => l.NomeNormalizado == normalizado, ct))
            return Conflict(Falha(NomeJaExiste(nome!)));

        var agora = DateTime.UtcNow;
        var loja = new Loja
        {
            Nome = nome!,
            NomeNormalizado = normalizado,
            CriadoEm = agora,
            AtualizadoEm = agora,
            CriadoPor = UsuarioId,
        };
        _db.lojas.Add(loja);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (EhNomeDuplicado(ex))
        {
            return Conflict(Falha(NomeJaExiste(nome!)));
        }

        return StatusCode(StatusCodes.Status201Created, new ResponseDto
        {
            Result = new { id = loja.LojaId, nome = loja.Nome },
            Message = "Loja criada.",
        });
    }

    /// <summary>Renomeia a loja.</summary>
    [HttpPut("{id:int}")]
    public async Task<ActionResult> Renomear(int id, [FromBody] LojaNomeDto dto, CancellationToken ct)
    {
        if (!PodeGerenciar) return Proibido();

        var (nome, erro) = ValidarNome(dto?.Nome);
        if (erro is not null) return BadRequest(Falha(erro));

        var loja = await _db.lojas.FirstOrDefaultAsync(l => l.LojaId == id, ct);
        if (loja is null) return NotFound(Falha(LojaNaoEncontrada));

        var normalizado = Loja.Normalizar(nome!);
        if (await _db.lojas.AnyAsync(l => l.NomeNormalizado == normalizado && l.LojaId != id, ct))
            return Conflict(Falha(NomeJaExiste(nome!)));

        loja.Nome = nome!;
        loja.NomeNormalizado = normalizado;
        loja.AtualizadoEm = DateTime.UtcNow;

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (EhNomeDuplicado(ex))
        {
            return Conflict(Falha(NomeJaExiste(nome!)));
        }

        return Ok(new ResponseDto { Result = new { id = loja.LojaId, nome = loja.Nome }, Message = "Loja renomeada." });
    }

    /// <summary>Remove a loja e todos os seus arquivos (cascata no banco).</summary>
    [HttpDelete("{id:int}")]
    public async Task<ActionResult> Remover(int id, CancellationToken ct)
    {
        if (!PodeGerenciar) return Proibido();

        // Exclusão direta no banco: não carrega a loja nem os arquivos (bytea) na memória
        var removidas = await _db.lojas.Where(l => l.LojaId == id).ExecuteDeleteAsync(ct);
        if (removidas == 0) return NotFound(Falha(LojaNaoEncontrada));

        return Ok(new ResponseDto { Message = "Loja removida." });
    }

    /// <summary>
    /// Envia um ou vários arquivos (campo "arquivos") para uma seção da loja, com descrição opcional
    /// (campo "descricao"). Todos são validados antes de gravar: se um for recusado, nada é gravado.
    /// </summary>
    [HttpPost("{id:int}/secoes/{secao}/arquivos")]
    [RequerServico(ServicosRestringiveis.Arquivos)]
    [RequestSizeLimit(LojaArquivoValidator.MaxBytesPorEnvio)]
    [RequestFormLimits(MultipartBodyLengthLimit = LojaArquivoValidator.MaxBytesPorEnvio)]
    public async Task<ActionResult> Enviar(int id, string secao, [FromForm] LojaUploadForm form, CancellationToken ct)
    {
        if (!PodeGerenciar) return Proibido();

        var secaoOficial = LojaSecoes.Encontrar(secao);
        if (secaoOficial is null)
            return BadRequest(Falha($"Seção inválida. Use uma destas: {string.Join(", ", LojaSecoes.Todas)}."));

        var loja = await _db.lojas.FirstOrDefaultAsync(l => l.LojaId == id, ct);
        if (loja is null) return NotFound(Falha(LojaNaoEncontrada));

        var arquivos = form?.Arquivos?.Where(a => a is not null).ToList() ?? new List<IFormFile>();
        if (arquivos.Count == 0) return BadRequest(Falha("Selecione ao menos um arquivo."));
        if (arquivos.Count > LojaArquivoValidator.MaxArquivosPorEnvio)
            return BadRequest(Falha($"Envie no máximo {LojaArquivoValidator.MaxArquivosPorEnvio} arquivos por vez."));

        var descricao = form?.Descricao?.Trim();
        if (string.IsNullOrEmpty(descricao)) descricao = null;
        if (descricao is not null && descricao.Length > LojaArquivo.DescricaoMaxLength)
            return BadRequest(Falha($"A descrição deve ter no máximo {LojaArquivo.DescricaoMaxLength} caracteres."));

        var agora = DateTime.UtcNow;
        var novos = new List<LojaArquivo>();
        foreach (var arquivo in arquivos)
        {
            var lido = await LojaArquivoValidator.LerAsync(arquivo, ct);
            if (lido.Erro is not null) return BadRequest(Falha(lido.Erro));

            novos.Add(new LojaArquivo
            {
                LojaId = loja.LojaId,
                Secao = secaoOficial,
                Tipo = lido.Tipo!,
                NomeArquivo = lido.NomeArquivo,
                ContentType = lido.ContentType!,
                Tamanho = lido.Conteudo!.LongLength,
                Conteudo = lido.Conteudo,
                Descricao = descricao,
                EnviadoPor = UsuarioId,
                CriadoEm = agora,
            });
        }

        _db.lojaArquivos.AddRange(novos);
        loja.AtualizadoEm = agora;
        await _db.SaveChangesAsync(ct);

        var resultado = novos.Select(a => new LojaArquivoDto
        {
            Id = a.LojaArquivoId,
            LojaId = a.LojaId,
            Secao = a.Secao,
            Tipo = a.Tipo,
            NomeArquivo = a.NomeArquivo,
            ContentType = a.ContentType,
            Tamanho = a.Tamanho,
            Descricao = a.Descricao,
            EnviadoPor = a.EnviadoPor,
            CriadoEm = a.CriadoEm,
        }).ToList();

        var mensagem = novos.Count == 1 ? "Arquivo enviado." : $"{novos.Count} arquivos enviados.";
        return StatusCode(StatusCodes.Status201Created, new ResponseDto { Result = resultado, Message = mensagem });
    }

    /// <summary>Remove um arquivo de uma seção.</summary>
    [HttpDelete("arquivos/{arquivoId:int}")]
    public async Task<ActionResult> RemoverArquivo(int arquivoId, CancellationToken ct)
    {
        if (!PodeGerenciar) return Proibido();

        var lojaId = await _db.lojaArquivos.AsNoTracking()
            .Where(a => a.LojaArquivoId == arquivoId)
            .Select(a => (int?)a.LojaId)
            .FirstOrDefaultAsync(ct);
        if (lojaId is null) return NotFound(Falha("Arquivo não encontrado."));

        await _db.lojaArquivos.Where(a => a.LojaArquivoId == arquivoId).ExecuteDeleteAsync(ct);
        await _db.lojas.Where(l => l.LojaId == lojaId.Value)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.AtualizadoEm, DateTime.UtcNow), ct);

        return Ok(new ResponseDto { Message = "Arquivo removido." });
    }
}

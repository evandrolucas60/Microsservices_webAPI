using System.Linq.Expressions;
using System.Text.Json;
using BuildingBlocks.Contracts.Administracao;
using BuildingBlocks.Shared.Security;
using Intranet.Services.GerenciamentoCategoriaAPI.Data;
using Intranet.Services.GerenciamentoCategoriaAPI.Models;
using Intranet.Services.GerenciamentoCategoriaAPI.Models.DTOs;
using Intranet.Services.GerenciamentoCategoriaAPI.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;
using Npgsql;

namespace Intranet.Services.GerenciamentoCategoriaAPI.Controllers;

/// <summary>
/// Categorias do GC (Gerenciamento por Categoria) e os seus itens (materiais/orientações: fotos e documentos).
/// A consulta (categorias, seções, itens e arquivos) é pública, inclusive para usuários anônimos — somente leitura;
/// criar/alterar/remover exige login e um dos papéis de gerenciamento
/// (padrão: superAdmin ou GC — veja <see cref="CategoriaPermissoes"/>).
/// </summary>
[Route("api/categorias")]
[ApiController]
[Authorize]
public class CategoriasController : ControllerBase
{
    private const string SemPermissao = "Apenas usuários com perfil superAdmin ou GC podem criar, alterar ou remover categorias e itens.";
    private const string CategoriaNaoEncontrada = "Categoria não encontrada.";
    private const string ItemNaoEncontrado = "Item não encontrado.";
    private const int OrdemMaxima = 9999;
    private const int TamanhoPaginaPadrao = 24;
    private const int TamanhoPaginaMaximo = 100;

    private readonly AppDbContext _db;
    private readonly CategoriaPermissoes _permissoes;

    public CategoriasController(AppDbContext db, CategoriaPermissoes permissoes)
    {
        _db = db;
        _permissoes = permissoes;
    }

    private static ResponseDto Falha(string message) => new() { IsSuccess = false, Message = message };

    private string? UsuarioId => User.FindFirst("id")?.Value;

    private bool PodeGerenciar => _permissoes.PodeGerenciar(User);

    private ObjectResult Proibido() => StatusCode(StatusCodes.Status403Forbidden, Falha(SemPermissao));

    /// <summary>Projeção sem o conteúdo binário (nunca carregar bytes em listagens).</summary>
    private static readonly Expression<Func<CategoriaItem, CategoriaItemDto>> ComoDto = i => new CategoriaItemDto
    {
        Id = i.CategoriaItemId,
        CategoriaId = i.CategoriaId,
        Titulo = i.Titulo,
        Descricao = i.Descricao,
        Secao = i.Secao,
        Tipo = i.Tipo,
        NomeArquivo = i.NomeArquivo,
        ContentType = i.ContentType,
        Tamanho = i.Tamanho,
        EnviadoEm = i.EnviadoEm,
        EnviadoPor = i.EnviadoPor,
    };

    private static CategoriaItemDto ParaDto(CategoriaItem i, bool comUsuario) => new()
    {
        Id = i.CategoriaItemId,
        CategoriaId = i.CategoriaId,
        Titulo = i.Titulo,
        Descricao = i.Descricao,
        Secao = i.Secao,
        Tipo = i.Tipo,
        NomeArquivo = i.NomeArquivo,
        ContentType = i.ContentType,
        Tamanho = i.Tamanho,
        EnviadoEm = i.EnviadoEm,
        EnviadoPor = comUsuario ? i.EnviadoPor : null,
    };

    /// <summary>Texto opcional: sem espaços nas pontas, vazio vira null. Retorna (valor, erro).</summary>
    private static (string? Valor, string? Erro) TextoOpcional(string? texto, int max, string campo)
    {
        var valor = texto?.Trim();
        if (string.IsNullOrEmpty(valor)) return (null, null);
        if (valor.Length > max) return (null, $"{campo} deve ter no máximo {max} caracteres.");
        return (valor, null);
    }

    /// <summary>Texto obrigatório de uma linha (nome/título). Retorna (valor, erro).</summary>
    private static (string? Valor, string? Erro) TextoObrigatorio(string? texto, int max, string campo, string vazio)
    {
        var valor = texto?.Trim();
        if (string.IsNullOrWhiteSpace(valor)) return (null, vazio);
        if (valor.Length > max) return (null, $"{campo} deve ter no máximo {max} caracteres.");
        if (valor.Any(char.IsControl)) return (null, $"{campo} contém caracteres inválidos.");
        return (valor, null);
    }

    /// <summary>Seção opcional: vazia = sem seção; senão precisa ser uma das seções do GC. Retorna (secao, erro).</summary>
    private static (string? Secao, string? Erro) ValidarSecao(string? secao)
    {
        if (string.IsNullOrWhiteSpace(secao)) return (null, null);
        var oficial = GCSecoes.Encontrar(secao);
        return oficial is null
            ? (null, $"Seção inválida. Use uma destas: {string.Join(", ", GCSecoes.Todas)}.")
            : (oficial, null);
    }

    private static bool EhNomeDuplicado(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    private static string NomeJaExiste(string nome) => $"Já existe uma categoria chamada \"{nome}\".";

    // Curingas do LIKE digitados pelo usuário são tratados como texto
    private static string EscaparLike(string valor) =>
        valor.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    // ---------------------------------------------------------------- consulta (pública, somente leitura)

    /// <summary>
    /// Permissões do usuário (a interface mostra ou esconde os controles de gerenciamento).
    /// Sem login devolve podeGerenciar = false.
    /// </summary>
    [HttpGet("permissoes")]
    [AllowAnonymous]
    public ActionResult Permissoes() => Ok(new ResponseDto { Result = new { podeGerenciar = PodeGerenciar } });

    /// <summary>Seções do GC, na ordem oficial (mesma lista das lojas).</summary>
    [HttpGet("secoes")]
    [AllowAnonymous]
    public ActionResult Secoes() => Ok(new ResponseDto { Result = GCSecoes.Todas });

    /// <summary>Categorias com a quantidade de itens, pela ordem definida (empate: nome).</summary>
    [HttpGet]
    [AllowAnonymous]
    public async Task<ActionResult> Listar(CancellationToken ct)
    {
        var comUsuario = PodeGerenciar;
        var categorias = await _db.categorias.AsNoTracking()
            .OrderBy(c => c.Ordem).ThenBy(c => c.Nome)
            .Select(c => new CategoriaDto
            {
                Id = c.CategoriaId,
                Nome = c.Nome,
                Descricao = c.Descricao,
                Ordem = c.Ordem,
                TotalItens = c.Itens.Count,
                CriadoEm = c.CriadoEm,
                AtualizadoEm = c.AtualizadoEm,
                CriadoPor = comUsuario ? c.CriadoPor : null,
            })
            .ToListAsync(ct);

        return Ok(new ResponseDto { Result = categorias });
    }

    /// <summary>Categoria com as quantidades de itens (total, por tipo e por seção).</summary>
    [HttpGet("{id:int}")]
    [AllowAnonymous]
    public async Task<ActionResult> Detalhe(int id, CancellationToken ct)
    {
        var categoria = await _db.categorias.AsNoTracking()
            .Where(c => c.CategoriaId == id)
            .Select(c => new { c.CategoriaId, c.Nome, c.Descricao, c.Ordem, c.CriadoEm, c.AtualizadoEm, c.CriadoPor })
            .FirstOrDefaultAsync(ct);
        if (categoria is null) return NotFound(Falha(CategoriaNaoEncontrada));

        var contagens = await _db.categoriaItens.AsNoTracking()
            .Where(i => i.CategoriaId == id)
            .GroupBy(i => new { i.Secao, i.Tipo })
            .Select(g => new { g.Key.Secao, g.Key.Tipo, Total = g.Count() })
            .ToListAsync(ct);

        var porSecao = GCSecoes.Todas
            .Select(s => new { Secao = s, Total = contagens.Where(c => c.Secao == s).Sum(c => c.Total) })
            .Where(s => s.Total > 0)
            .ToDictionary(s => s.Secao, s => s.Total);

        return Ok(new ResponseDto
        {
            Result = new CategoriaDetalheDto
            {
                Id = categoria.CategoriaId,
                Nome = categoria.Nome,
                Descricao = categoria.Descricao,
                Ordem = categoria.Ordem,
                CriadoEm = categoria.CriadoEm,
                AtualizadoEm = categoria.AtualizadoEm,
                CriadoPor = PodeGerenciar ? categoria.CriadoPor : null,
                TotalItens = contagens.Sum(c => c.Total),
                TotalImagens = contagens.Where(c => c.Tipo == CategoriaItemTipos.Imagem).Sum(c => c.Total),
                TotalDocumentos = contagens.Where(c => c.Tipo == CategoriaItemTipos.Documento).Sum(c => c.Total),
                ItensPorSecao = porSecao,
            }
        });
    }

    /// <summary>
    /// Itens da categoria (somente metadados), do mais recente para o mais antigo, paginados (X-Pagination).
    /// busca: parte do título, da descrição ou do nome do arquivo; secao: uma das seções do GC;
    /// tipo: imagem ou documento.
    /// </summary>
    [HttpGet("{id:int}/itens")]
    [AllowAnonymous]
    public async Task<ActionResult> Itens(
        int id,
        [FromQuery] string? busca,
        [FromQuery] string? secao,
        [FromQuery] string? tipo,
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = TamanhoPaginaPadrao,
        CancellationToken ct = default)
    {
        pageNumber = Math.Max(pageNumber, 1);
        pageSize = pageSize < 1 ? TamanhoPaginaPadrao : Math.Min(pageSize, TamanhoPaginaMaximo);

        if (!CategoriaItemTipos.TentarInterpretar(tipo, out var tipoFiltro))
            return BadRequest(Falha("Tipo inválido. Use imagem ou documento."));

        var (secaoFiltro, erroSecao) = ValidarSecao(secao);
        if (erroSecao is not null) return BadRequest(Falha(erroSecao));

        if (!await _db.categorias.AnyAsync(c => c.CategoriaId == id, ct))
            return NotFound(Falha(CategoriaNaoEncontrada));

        var query = _db.categoriaItens.AsNoTracking().Where(i => i.CategoriaId == id);
        if (tipoFiltro is not null) query = query.Where(i => i.Tipo == tipoFiltro);
        if (secaoFiltro is not null) query = query.Where(i => i.Secao == secaoFiltro);

        var termo = busca?.Trim();
        if (!string.IsNullOrEmpty(termo))
        {
            var padrao = $"%{EscaparLike(termo)}%";
            query = query.Where(i => EF.Functions.ILike(i.Titulo, padrao, "\\")
                                     || (i.Descricao != null && EF.Functions.ILike(i.Descricao, padrao, "\\"))
                                     || EF.Functions.ILike(i.NomeArquivo, padrao, "\\"));
        }

        var total = await query.CountAsync(ct);
        var itens = await query
            .OrderByDescending(i => i.EnviadoEm).ThenByDescending(i => i.CategoriaItemId)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .Select(ComoDto)
            .ToListAsync(ct);

        // Ids de usuário só para quem gerencia
        if (!PodeGerenciar) itens.ForEach(i => i.EnviadoPor = null);

        var totalPages = (int)Math.Ceiling(total / (double)pageSize);
        Response.Headers["X-Pagination"] = JsonSerializer.Serialize(new
        {
            TotalCount = total,
            PageSize = pageSize,
            CurrentPage = pageNumber,
            TotalPages = totalPages,
            HasNext = pageNumber < totalPages,
            HasPrevious = pageNumber > 1,
        });

        return Ok(new ResponseDto { Result = itens });
    }

    /// <summary>
    /// Conteúdo do arquivo do item: imagem exibida no navegador, documento sempre como download.
    /// Público como o restante da consulta.
    /// </summary>
    [HttpGet("itens/{itemId:int}/arquivo")]
    [AllowAnonymous]
    public async Task<ActionResult> Baixar(int itemId, CancellationToken ct)
    {
        var arquivo = await _db.categoriaItens.AsNoTracking()
            .Where(i => i.CategoriaItemId == itemId)
            .Select(i => new { i.Conteudo, i.ContentType, i.NomeArquivo, i.Tipo })
            .FirstOrDefaultAsync(ct);
        if (arquivo is null) return NotFound(Falha(ItemNaoEncontrado));

        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers.CacheControl = "private, max-age=86400";

        if (arquivo.Tipo == CategoriaItemTipos.Imagem)
        {
            var inline = new ContentDispositionHeaderValue("inline");
            inline.SetHttpFileName(arquivo.NomeArquivo);
            Response.Headers.ContentDisposition = inline.ToString();
            return File(arquivo.Conteudo, arquivo.ContentType);
        }

        // Documentos: "attachment" com o nome do arquivo (nunca renderizados pelo navegador)
        return File(arquivo.Conteudo, arquivo.ContentType, arquivo.NomeArquivo);
    }

    // ---------------------------------------------------------------- gerenciamento: categorias

    /// <summary>Cria uma categoria (nome único, sem diferenciar maiúsculas de minúsculas). Sem ordem: vai para o fim.</summary>
    [HttpPost]
    public async Task<ActionResult> Criar([FromBody] CategoriaSalvarDto dto, CancellationToken ct)
    {
        if (!PodeGerenciar) return Proibido();

        var (nome, erroNome) = TextoObrigatorio(dto?.Nome, Categoria.NomeMaxLength, "O nome da categoria", "Informe o nome da categoria.");
        if (erroNome is not null) return BadRequest(Falha(erroNome));
        var (descricao, erroDescricao) = TextoOpcional(dto?.Descricao, Categoria.DescricaoMaxLength, "A descrição");
        if (erroDescricao is not null) return BadRequest(Falha(erroDescricao));
        if (dto?.Ordem is < 0 or > OrdemMaxima) return BadRequest(Falha($"A ordem deve estar entre 0 e {OrdemMaxima}."));

        var normalizado = Categoria.Normalizar(nome!);
        if (await _db.categorias.AnyAsync(c => c.NomeNormalizado == normalizado, ct))
            return BadRequest(Falha(NomeJaExiste(nome!)));

        var ordem = dto?.Ordem ?? Math.Min(OrdemMaxima, (await _db.categorias.MaxAsync(c => (int?)c.Ordem, ct) ?? 0) + 1);

        var agora = DateTime.UtcNow;
        var categoria = new Categoria
        {
            Nome = nome!,
            NomeNormalizado = normalizado,
            Descricao = descricao,
            Ordem = ordem,
            CriadoPor = UsuarioId,
            CriadoEm = agora,
            AtualizadoEm = agora,
        };
        _db.categorias.Add(categoria);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (EhNomeDuplicado(ex))
        {
            return BadRequest(Falha(NomeJaExiste(nome!)));
        }

        return StatusCode(StatusCodes.Status201Created, new ResponseDto
        {
            Result = new { id = categoria.CategoriaId, nome = categoria.Nome, ordem = categoria.Ordem },
            Message = "Categoria criada.",
        });
    }

    /// <summary>Altera nome, descrição e (se informada) a ordem da categoria.</summary>
    [HttpPut("{id:int}")]
    public async Task<ActionResult> Alterar(int id, [FromBody] CategoriaSalvarDto dto, CancellationToken ct)
    {
        if (!PodeGerenciar) return Proibido();

        var (nome, erroNome) = TextoObrigatorio(dto?.Nome, Categoria.NomeMaxLength, "O nome da categoria", "Informe o nome da categoria.");
        if (erroNome is not null) return BadRequest(Falha(erroNome));
        var (descricao, erroDescricao) = TextoOpcional(dto?.Descricao, Categoria.DescricaoMaxLength, "A descrição");
        if (erroDescricao is not null) return BadRequest(Falha(erroDescricao));
        if (dto?.Ordem is < 0 or > OrdemMaxima) return BadRequest(Falha($"A ordem deve estar entre 0 e {OrdemMaxima}."));

        var categoria = await _db.categorias.FirstOrDefaultAsync(c => c.CategoriaId == id, ct);
        if (categoria is null) return NotFound(Falha(CategoriaNaoEncontrada));

        var normalizado = Categoria.Normalizar(nome!);
        if (await _db.categorias.AnyAsync(c => c.NomeNormalizado == normalizado && c.CategoriaId != id, ct))
            return BadRequest(Falha(NomeJaExiste(nome!)));

        categoria.Nome = nome!;
        categoria.NomeNormalizado = normalizado;
        categoria.Descricao = descricao;
        if (dto?.Ordem is int ordem) categoria.Ordem = ordem;
        categoria.AtualizadoEm = DateTime.UtcNow;

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (EhNomeDuplicado(ex))
        {
            return BadRequest(Falha(NomeJaExiste(nome!)));
        }

        return Ok(new ResponseDto
        {
            Result = new { id = categoria.CategoriaId, nome = categoria.Nome, ordem = categoria.Ordem },
            Message = "Categoria alterada.",
        });
    }

    /// <summary>Remove a categoria e todos os seus itens (cascata no banco).</summary>
    [HttpDelete("{id:int}")]
    public async Task<ActionResult> Remover(int id, CancellationToken ct)
    {
        if (!PodeGerenciar) return Proibido();

        // Exclusão direta no banco: não carrega a categoria nem os arquivos (bytea) na memória
        var removidas = await _db.categorias.Where(c => c.CategoriaId == id).ExecuteDeleteAsync(ct);
        if (removidas == 0) return NotFound(Falha(CategoriaNaoEncontrada));

        return Ok(new ResponseDto { Message = "Categoria removida." });
    }

    // ---------------------------------------------------------------- gerenciamento: itens

    /// <summary>
    /// Envia um item para a categoria (multipart: titulo, descricao, secao e arquivo).
    /// O arquivo é validado pelo conteúdo (imagens JPG/PNG/GIF/WebP até 10 MB ou documentos até 20 MB).
    /// </summary>
    [HttpPost("{id:int}/itens")]
    [RequerServico(ServicosRestringiveis.Arquivos)]
    [RequestSizeLimit(CategoriaArquivoValidator.MaxBytesPorEnvio)]
    [RequestFormLimits(MultipartBodyLengthLimit = CategoriaArquivoValidator.MaxBytesPorEnvio)]
    public async Task<ActionResult> Enviar(int id, [FromForm] CategoriaItemUploadForm form, CancellationToken ct)
    {
        if (!PodeGerenciar) return Proibido();

        var (titulo, erroTitulo) = TextoObrigatorio(form?.Titulo, CategoriaItem.TituloMaxLength, "O título", "Informe o título do item.");
        if (erroTitulo is not null) return BadRequest(Falha(erroTitulo));
        var (descricao, erroDescricao) = TextoOpcional(form?.Descricao, CategoriaItem.DescricaoMaxLength, "A descrição");
        if (erroDescricao is not null) return BadRequest(Falha(erroDescricao));
        var (secao, erroSecao) = ValidarSecao(form?.Secao);
        if (erroSecao is not null) return BadRequest(Falha(erroSecao));

        var categoria = await _db.categorias.FirstOrDefaultAsync(c => c.CategoriaId == id, ct);
        if (categoria is null) return NotFound(Falha(CategoriaNaoEncontrada));

        var lido = await CategoriaArquivoValidator.LerAsync(form?.Arquivo, ct);
        if (lido.Erro is not null) return BadRequest(Falha(lido.Erro));

        var agora = DateTime.UtcNow;
        var item = new CategoriaItem
        {
            CategoriaId = categoria.CategoriaId,
            Titulo = titulo!,
            Descricao = descricao,
            Secao = secao,
            Tipo = lido.Tipo!,
            NomeArquivo = lido.NomeArquivo,
            ContentType = lido.ContentType!,
            Tamanho = lido.Conteudo!.LongLength,
            Conteudo = lido.Conteudo,
            EnviadoPor = UsuarioId,
            EnviadoEm = agora,
        };
        _db.categoriaItens.Add(item);
        categoria.AtualizadoEm = agora;
        await _db.SaveChangesAsync(ct);

        return StatusCode(StatusCodes.Status201Created, new ResponseDto
        {
            Result = ParaDto(item, comUsuario: true),
            Message = "Item enviado.",
        });
    }

    /// <summary>Altera título, descrição e seção de um item (o arquivo não muda).</summary>
    [HttpPut("itens/{itemId:int}")]
    public async Task<ActionResult> AlterarItem(int itemId, [FromBody] CategoriaItemAlterarDto dto, CancellationToken ct)
    {
        if (!PodeGerenciar) return Proibido();

        var (titulo, erroTitulo) = TextoObrigatorio(dto?.Titulo, CategoriaItem.TituloMaxLength, "O título", "Informe o título do item.");
        if (erroTitulo is not null) return BadRequest(Falha(erroTitulo));
        var (descricao, erroDescricao) = TextoOpcional(dto?.Descricao, CategoriaItem.DescricaoMaxLength, "A descrição");
        if (erroDescricao is not null) return BadRequest(Falha(erroDescricao));
        var (secao, erroSecao) = ValidarSecao(dto?.Secao);
        if (erroSecao is not null) return BadRequest(Falha(erroSecao));

        var agora = DateTime.UtcNow;
        // Atualização direta no banco: não carrega o conteúdo (bytea)
        var alterados = await _db.categoriaItens
            .Where(i => i.CategoriaItemId == itemId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(i => i.Titulo, titulo!)
                .SetProperty(i => i.Descricao, descricao)
                .SetProperty(i => i.Secao, secao), ct);
        if (alterados == 0) return NotFound(Falha(ItemNaoEncontrado));

        var item = await _db.categoriaItens.AsNoTracking()
            .Where(i => i.CategoriaItemId == itemId)
            .Select(ComoDto)
            .FirstAsync(ct);
        await _db.categorias.Where(c => c.CategoriaId == item.CategoriaId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.AtualizadoEm, agora), ct);

        return Ok(new ResponseDto { Result = item, Message = "Item alterado." });
    }

    /// <summary>Remove um item da categoria.</summary>
    [HttpDelete("itens/{itemId:int}")]
    public async Task<ActionResult> RemoverItem(int itemId, CancellationToken ct)
    {
        if (!PodeGerenciar) return Proibido();

        var categoriaId = await _db.categoriaItens.AsNoTracking()
            .Where(i => i.CategoriaItemId == itemId)
            .Select(i => (int?)i.CategoriaId)
            .FirstOrDefaultAsync(ct);
        if (categoriaId is null) return NotFound(Falha(ItemNaoEncontrado));

        await _db.categoriaItens.Where(i => i.CategoriaItemId == itemId).ExecuteDeleteAsync(ct);
        await _db.categorias.Where(c => c.CategoriaId == categoriaId.Value)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.AtualizadoEm, DateTime.UtcNow), ct);

        return Ok(new ResponseDto { Message = "Item removido." });
    }
}

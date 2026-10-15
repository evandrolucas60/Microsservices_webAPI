using System.Globalization;
using System.Text.RegularExpressions;
using BuildingBlocks.Contracts.Administracao;
using BuildingBlocks.Contracts.Identidade;
using BuildingBlocks.Shared.Exceptions;
using BuildingBlocks.Shared.Messaging;
using Intranet.Services.AuthAPI.Data;
using Intranet.Services.AuthAPI.Models;
using Intranet.Services.AuthAPI.Models.DTOs;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using IPublishEndpoint = MassTransit.IPublishEndpoint;

namespace Intranet.Services.AuthAPI.Service;

/// <summary>
/// Regras do painel de gestão (superAdmin): usuários, bloqueio, restrições por serviço e papéis.
/// </summary>
public class AdminUsuariosService
{
    private const int MaxMotivo = 300;
    private const int MaxNomeRole = 50;
    private const string CacheMembros = "admin:membros:todos";
    private static readonly Regex NomeRoleValido = new("^[A-Za-z0-9._-]+$", RegexOptions.Compiled);
    private static readonly string[] RolesProtegidos = { Program.SuperAdminRole, Program.AdminRole };

    private readonly AppDbContext _db;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly RoleManager<IdentityRole> _roleManager;
    private readonly IIdentidadeGateway _identidade;
    private readonly IPublishEndpoint _publish;
    private readonly IMemoryCache _cache;
    private readonly ILogger<AdminUsuariosService> _logger;
    private readonly string[] _superAdminRoles;

    public AdminUsuariosService(
        AppDbContext db,
        UserManager<ApplicationUser> userManager,
        RoleManager<IdentityRole> roleManager,
        IIdentidadeGateway identidade,
        IPublishEndpoint publish,
        IMemoryCache cache,
        IConfiguration configuration,
        ILogger<AdminUsuariosService> logger)
    {
        _db = db;
        _userManager = userManager;
        _roleManager = roleManager;
        _identidade = identidade;
        _publish = publish;
        _cache = cache;
        _logger = logger;
        // Mesmos papéis aceitos pela política AdminPolicies.SuperAdmin
        _superAdminRoles = configuration.GetSection("Administracao:Roles").Get<string[]>() is { Length: > 0 } configured
            ? configured
            : new[] { Program.SuperAdminRole };
    }

    // ---------------------------------------------------------------- bloqueio (lockout do Identity)

    /// <summary>Bloqueio vigente: lockout habilitado e com fim no futuro.</summary>
    public static bool EstaBloqueado(ApplicationUser user) =>
        user.LockoutEnabled && user.LockoutEnd is { } fim && fim > DateTimeOffset.UtcNow;

    /// <summary>Fim do bloqueio em UTC; null quando indeterminado (DateTimeOffset.MaxValue) ou sem bloqueio.</summary>
    public static DateTime? BloqueadoAte(ApplicationUser user) =>
        EstaBloqueado(user) && user.LockoutEnd!.Value.Year < 9999 ? user.LockoutEnd.Value.UtcDateTime : null;

    private bool IsSuperAdminRole(string? role) =>
        role is not null && _superAdminRoles.Any(r => string.Equals(r.Trim(), role.Trim(), StringComparison.OrdinalIgnoreCase));

    // ---------------------------------------------------------------- consultas

    public async Task<(List<UsuarioAdminDto> Itens, int Total)> ListarAsync(
        string? busca, string? role, bool? bloqueados, int pageNumber, int pageSize, CancellationToken ct)
    {
        var agora = DateTimeOffset.UtcNow;
        var query = _db.Users.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(busca))
        {
            var termo = busca.Trim();
            var termoNormalizado = termo.ToUpperInvariant();
            // Nome do colaborador (IdentidadeAPI): ids cujo nome contém o termo
            var idsPorNome = await BuscarIdsPorNomeAsync(termo, ct);
            query = query.Where(u =>
                (u.NormalizedEmail != null && u.NormalizedEmail.Contains(termoNormalizado)) ||
                (u.NormalizedUserName != null && u.NormalizedUserName.Contains(termoNormalizado)) ||
                idsPorNome.Contains(u.Id));
        }

        if (!string.IsNullOrWhiteSpace(role))
        {
            var roleNormalizado = role.Trim().ToUpperInvariant();
            query = query.Where(u => _db.UserRoles.Any(ur => ur.UserId == u.Id &&
                _db.Roles.Any(r => r.Id == ur.RoleId && r.NormalizedName == roleNormalizado)));
        }

        if (bloqueados is true)
            query = query.Where(u => u.LockoutEnabled && u.LockoutEnd != null && u.LockoutEnd > agora);
        else if (bloqueados is false)
            query = query.Where(u => !u.LockoutEnabled || u.LockoutEnd == null || u.LockoutEnd <= agora);

        var total = await query.CountAsync(ct);
        var usuarios = await query
            .OrderBy(u => u.Email)
            .ThenBy(u => u.Id)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return (await MontarAsync(usuarios, ct), total);
    }

    public async Task<UsuarioAdminDto> ObterAsync(string userId, CancellationToken ct)
    {
        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new NotFoundException("Usuário não encontrado.");
        return (await MontarAsync(new List<ApplicationUser> { user }, ct))[0];
    }

    private async Task<List<UsuarioAdminDto>> MontarAsync(List<ApplicationUser> usuarios, CancellationToken ct)
    {
        var ids = usuarios.Select(u => u.Id).ToList();
        if (ids.Count == 0) return new List<UsuarioAdminDto>();

        var roles = (await (from ur in _db.UserRoles
                            join r in _db.Roles on ur.RoleId equals r.Id
                            where ids.Contains(ur.UserId)
                            select new { ur.UserId, r.Name })
                .ToListAsync(ct))
            .GroupBy(x => x.UserId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Name!).OrderBy(n => n).ToArray());

        var restricoes = await _db.UserRestrictions.AsNoTracking()
            .Where(r => ids.Contains(r.UserId))
            .ToDictionaryAsync(r => r.UserId, ct);

        var membros = await ObterMembrosAsync(ids, ct);

        return usuarios.Select(u =>
        {
            restricoes.TryGetValue(u.Id, out var restricao);
            membros.TryGetValue(u.Id, out var membro);
            var bloqueado = EstaBloqueado(u);
            return new UsuarioAdminDto
            {
                UserId = u.Id,
                Email = u.Email,
                UserName = u.UserName,
                Roles = roles.TryGetValue(u.Id, out var r) ? r : Array.Empty<string>(),
                Bloqueado = bloqueado,
                BloqueadoAte = BloqueadoAte(u),
                MotivoBloqueio = bloqueado ? restricao?.MotivoBloqueio : null,
                Restricoes = restricao?.Servicos ?? Array.Empty<string>(),
                MemberId = membro?.MemberId,
                Nome = membro?.Nome,
                Foto = membro?.Foto,
                Departamento = membro?.Departamento,
                Cargo = membro?.Cargo,
                GestorMemberId = membro?.GestorMemberId,
                UltimoAcesso = membro?.UltimoAcesso,
            };
        }).ToList();
    }

    /// <summary>Colaboradores das contas (IdentidadeAPI). Indisponível = dicionário vazio (campos nulos).</summary>
    private async Task<IReadOnlyDictionary<string, MembroDeUsuario>> ObterMembrosAsync(IEnumerable<string> userIds, CancellationToken ct)
    {
        try
        {
            return await _identidade.ObterMembrosPorUsuariosAsync(userIds, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Não foi possível obter os colaboradores no IdentidadeAPI; dados do colaborador omitidos");
            return new Dictionary<string, MembroDeUsuario>();
        }
    }

    /// <summary>
    /// Busca por nome: carrega (em lotes de 500, com cache de 2 minutos) o nome do colaborador de
    /// todas as contas e filtra sem diferenciar maiúsculas/acentos. Sem IdentidadeAPI, não filtra por nome.
    /// </summary>
    private async Task<List<string>> BuscarIdsPorNomeAsync(string termo, CancellationToken ct)
    {
        if (!_cache.TryGetValue(CacheMembros, out Dictionary<string, string>? nomes) || nomes is null)
        {
            nomes = new Dictionary<string, string>();
            var todos = await _db.Users.AsNoTracking().Select(u => u.Id).ToListAsync(ct);
            try
            {
                foreach (var lote in todos.Chunk(500))
                {
                    foreach (var (userId, membro) in await _identidade.ObterMembrosPorUsuariosAsync(lote, ct))
                    {
                        if (!string.IsNullOrWhiteSpace(membro.Nome)) nomes[userId] = membro.Nome;
                    }
                }
                _cache.Set(CacheMembros, nomes, TimeSpan.FromMinutes(2));
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Busca por nome indisponível (IdentidadeAPI não respondeu)");
                return new List<string>();
            }
        }

        var comparador = CultureInfo.GetCultureInfo("pt-BR").CompareInfo;
        return nomes
            .Where(n => comparador.IndexOf(n.Value, termo, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0)
            .Select(n => n.Key)
            .ToList();
    }

    // ---------------------------------------------------------------- bloqueio e restrições

    public async Task<UsuarioAdminDto> BloquearAsync(string adminUserId, string userId, BloquearUsuarioRequest request, CancellationToken ct)
    {
        var motivo = request.Motivo?.Trim();
        if (string.IsNullOrEmpty(motivo)) throw new BusinessRuleException("Informe o motivo do bloqueio.");
        if (motivo.Length > MaxMotivo) throw new BusinessRuleException($"O motivo deve ter no máximo {MaxMotivo} caracteres.");

        DateTime? ate = request.Ate is { } data
            ? (data.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(data, DateTimeKind.Utc) : data.ToUniversalTime())
            : null;
        if (ate is not null && ate <= DateTime.UtcNow) throw new BusinessRuleException("A data final do bloqueio deve estar no futuro.");

        if (string.Equals(adminUserId, userId, StringComparison.OrdinalIgnoreCase))
            throw new BusinessRuleException("Você não pode bloquear a sua própria conta.");

        var user = await _userManager.FindByIdAsync(userId) ?? throw new NotFoundException("Usuário não encontrado.");
        if ((await _userManager.GetRolesAsync(user)).Any(IsSuperAdminRole))
            throw new BusinessRuleException("Não é possível bloquear outro superAdmin.");

        var restricao = await ObterOuCriarRestricaoAsync(userId, ct);
        var agora = DateTime.UtcNow;
        restricao.MotivoBloqueio = motivo;
        restricao.BloqueadoEm = agora;
        restricao.BloqueadoPorUserId = adminUserId;
        restricao.AtualizadoPorUserId = adminUserId;
        restricao.AtualizadoEm = agora;

        // Lockout do Identity: o login é recusado até LockoutEnd (MaxValue = indeterminado).
        // O refresh token é revogado para a sessão atual não ser renovada.
        user.LockoutEnabled = true;
        user.LockoutEnd = ate is { } fim ? new DateTimeOffset(fim, TimeSpan.Zero) : DateTimeOffset.MaxValue;
        user.RefreshToken = null;
        await SalvarUsuarioAsync(user, ct);

        _logger.LogInformation("Usuário {UserId} bloqueado por {AdminId} até {Ate}: {Motivo}", userId, adminUserId, ate?.ToString("o") ?? "indeterminado", motivo);
        await PublicarAlteracaoAsync(user, restricao, ct);
        return await ObterAsync(userId, ct);
    }

    public async Task<UsuarioAdminDto> DesbloquearAsync(string adminUserId, string userId, CancellationToken ct)
    {
        var user = await _userManager.FindByIdAsync(userId) ?? throw new NotFoundException("Usuário não encontrado.");

        var restricao = await _db.UserRestrictions.FirstOrDefaultAsync(r => r.UserId == userId, ct);
        if (restricao is not null)
        {
            restricao.MotivoBloqueio = null;
            restricao.BloqueadoEm = null;
            restricao.BloqueadoPorUserId = null;
            restricao.AtualizadoPorUserId = adminUserId;
            restricao.AtualizadoEm = DateTime.UtcNow;
        }

        user.LockoutEnd = null;
        user.AccessFailedCount = 0;
        await SalvarUsuarioAsync(user, ct);

        _logger.LogInformation("Usuário {UserId} desbloqueado por {AdminId}", userId, adminUserId);
        await PublicarAlteracaoAsync(user, restricao, ct);
        return await ObterAsync(userId, ct);
    }

    public async Task<UsuarioAdminDto> DefinirRestricoesAsync(string adminUserId, string userId, RestricoesUsuarioRequest request, CancellationToken ct)
    {
        var servicos = (request.Servicos ?? Array.Empty<string>())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim().ToLowerInvariant())
            .Distinct()
            .ToArray();
        var invalidos = servicos.Where(s => !ServicosRestringiveis.IsValid(s)).ToArray();
        if (invalidos.Length > 0) throw new BusinessRuleException($"Serviço(s) inválido(s): {string.Join(", ", invalidos)}.");

        var motivo = string.IsNullOrWhiteSpace(request.Motivo) ? null : request.Motivo.Trim();
        if (motivo?.Length > MaxMotivo) throw new BusinessRuleException($"O motivo deve ter no máximo {MaxMotivo} caracteres.");

        var user = await _userManager.FindByIdAsync(userId) ?? throw new NotFoundException("Usuário não encontrado.");

        var restricao = await ObterOuCriarRestricaoAsync(userId, ct);
        restricao.Servicos = servicos;
        restricao.MotivoRestricao = servicos.Length == 0 ? null : motivo;
        restricao.AtualizadoPorUserId = adminUserId;
        restricao.AtualizadoEm = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Restrições do usuário {UserId} alteradas por {AdminId}: [{Servicos}]", userId, adminUserId, string.Join(", ", servicos));
        await PublicarAlteracaoAsync(user, restricao, ct);
        return await ObterAsync(userId, ct);
    }

    private async Task<UserRestriction> ObterOuCriarRestricaoAsync(string userId, CancellationToken ct)
    {
        var restricao = await _db.UserRestrictions.FirstOrDefaultAsync(r => r.UserId == userId, ct);
        if (restricao is null)
        {
            restricao = new UserRestriction { UserId = userId, AtualizadoEm = DateTime.UtcNow };
            _db.UserRestrictions.Add(restricao);
        }
        return restricao;
    }

    // O UserManager usa o mesmo AppDbContext (escopo da requisição): UpdateAsync grava também a restrição
    private async Task SalvarUsuarioAsync(ApplicationUser user, CancellationToken ct)
    {
        var result = await _userManager.UpdateAsync(user);
        if (!result.Succeeded)
            throw new BusinessRuleException("Não foi possível atualizar o usuário: " + string.Join(" ", result.Errors.Select(e => e.Description)));
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Avisa os demais serviços (cache de bloqueio/restrições). Falha ao publicar não desfaz a alteração.</summary>
    private async Task PublicarAlteracaoAsync(ApplicationUser user, UserRestriction? restricao, CancellationToken ct)
    {
        try
        {
            await _publish.Publish(new RestricoesUsuarioAlteradas
            {
                UserId = user.Id,
                Bloqueado = EstaBloqueado(user),
                BloqueadoAte = BloqueadoAte(user),
                Servicos = restricao?.Servicos ?? Array.Empty<string>(),
                Em = DateTime.UtcNow,
            }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Não foi possível publicar RestricoesUsuarioAlteradas do usuário {UserId}; os serviços atualizam pelo cache (até 5 min)", user.Id);
        }
    }

    // ---------------------------------------------------------------- papéis (roles)

    public async Task<List<RoleDto>> ListarRolesAsync(CancellationToken ct) =>
        await _db.Roles.AsNoTracking()
            .OrderBy(r => r.Name)
            .Select(r => new RoleDto
            {
                Nome = r.Name!,
                TotalUsuarios = _db.UserRoles.Count(ur => ur.RoleId == r.Id),
            })
            .ToListAsync(ct);

    public async Task<RoleDto> CriarRoleAsync(string? nome)
    {
        nome = nome?.Trim();
        if (string.IsNullOrEmpty(nome)) throw new BusinessRuleException("Informe o nome do papel.");
        if (nome.Length > MaxNomeRole) throw new BusinessRuleException($"O nome do papel deve ter no máximo {MaxNomeRole} caracteres.");
        if (!NomeRoleValido.IsMatch(nome)) throw new BusinessRuleException("O nome do papel aceita apenas letras, números, ponto, hífen e sublinhado.");

        // RoleExistsAsync compara pelo nome normalizado (sem diferenciar maiúsculas)
        if (await _roleManager.RoleExistsAsync(nome)) throw new BusinessRuleException("Já existe um papel com esse nome.");

        var result = await _roleManager.CreateAsync(new IdentityRole(nome));
        if (!result.Succeeded)
            throw new BusinessRuleException("Não foi possível criar o papel: " + string.Join(" ", result.Errors.Select(e => e.Description)));

        _logger.LogInformation("Papel {Role} criado", nome);
        return new RoleDto { Nome = nome };
    }

    public async Task ExcluirRoleAsync(string nome)
    {
        if (RolesProtegidos.Any(p => string.Equals(p, nome?.Trim(), StringComparison.OrdinalIgnoreCase)) || IsSuperAdminRole(nome))
            throw new BusinessRuleException($"O papel {nome} é do sistema e não pode ser excluído.");

        var role = await _roleManager.FindByNameAsync(nome) ?? throw new NotFoundException("Papel não encontrado.");
        if (await _db.UserRoles.AnyAsync(ur => ur.RoleId == role.Id))
            throw new BusinessRuleException("O papel possui usuários. Remova-os antes de excluir.");

        var result = await _roleManager.DeleteAsync(role);
        if (!result.Succeeded)
            throw new BusinessRuleException("Não foi possível excluir o papel: " + string.Join(" ", result.Errors.Select(e => e.Description)));

        _logger.LogInformation("Papel {Role} excluído", role.Name);
    }

    public async Task<UsuarioAdminDto> AdicionarRoleAsync(string adminUserId, string userId, string? roleNome, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(roleNome)) throw new BusinessRuleException("Informe o papel.");
        var role = await _roleManager.FindByNameAsync(roleNome.Trim()) ?? throw new NotFoundException("Papel não encontrado.");
        var user = await _userManager.FindByIdAsync(userId) ?? throw new NotFoundException("Usuário não encontrado.");

        if (!await _userManager.IsInRoleAsync(user, role.Name!))
        {
            var result = await _userManager.AddToRoleAsync(user, role.Name!);
            if (!result.Succeeded)
                throw new BusinessRuleException("Não foi possível atribuir o papel: " + string.Join(" ", result.Errors.Select(e => e.Description)));
            _logger.LogInformation("Papel {Role} atribuído ao usuário {UserId} por {AdminId}", role.Name, userId, adminUserId);
        }

        return await ObterAsync(userId, ct);
    }

    public async Task<UsuarioAdminDto> RemoverRoleAsync(string adminUserId, string userId, string roleNome, CancellationToken ct)
    {
        var role = await _roleManager.FindByNameAsync(roleNome.Trim()) ?? throw new NotFoundException("Papel não encontrado.");
        var user = await _userManager.FindByIdAsync(userId) ?? throw new NotFoundException("Usuário não encontrado.");

        if (!await _userManager.IsInRoleAsync(user, role.Name!)) return await ObterAsync(userId, ct);

        if (IsSuperAdminRole(role.Name))
        {
            if (string.Equals(adminUserId, userId, StringComparison.OrdinalIgnoreCase))
                throw new BusinessRuleException("Você não pode remover o seu próprio papel superAdmin.");
            if (await _db.UserRoles.CountAsync(ur => ur.RoleId == role.Id, ct) <= 1)
                throw new BusinessRuleException("Não é possível remover o último superAdmin.");
        }

        var result = await _userManager.RemoveFromRoleAsync(user, role.Name!);
        if (!result.Succeeded)
            throw new BusinessRuleException("Não foi possível remover o papel: " + string.Join(" ", result.Errors.Select(e => e.Description)));

        _logger.LogInformation("Papel {Role} removido do usuário {UserId} por {AdminId}", role.Name, userId, adminUserId);
        return await ObterAsync(userId, ct);
    }
}

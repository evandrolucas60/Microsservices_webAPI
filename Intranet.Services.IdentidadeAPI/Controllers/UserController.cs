using AutoMapper;
using Intranet.Services.IdentidadeAPI.Data;
using Intranet.Services.IdentidadeAPI.Models;
using Intranet.Services.IdentidadeAPI.Models.DTOs;
using Intranet.Services.IdentidadeAPI.Repository.IRepository;
using Intranet.Services.IdentidadeAPI.Service.IService;
using Intranet.Services.IdentidadeAPI.Utility;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace Intranet.Services.IdentidadeAPI.Controllers;

[Route("api/[controller]")]
[ApiController]
[Authorize]
public class UserController : ControllerBase
{
    private readonly IUnityOfWork _uof;
    private readonly AppDbContext _db;
    private readonly IWorkService _workService;
    private readonly IEducationService _educationService;
    private readonly IOwnershipChecker _ownership;
    private readonly IMapper _mapper;
    private ResponseDto _response;
    private readonly ILogger<UserController> _logger;

    public UserController(
        IUnityOfWork unityOfWork,
        AppDbContext db,
        IWorkService workService,
        IEducationService educationService,
        IOwnershipChecker ownership,
        IMapper mapper,
        ILogger<UserController> logger)
    {
        _uof = unityOfWork;
        _db = db;
        _workService = workService;
        _educationService = educationService;
        _ownership = ownership;
        _mapper = mapper;
        _response = new ResponseDto();
        _logger = logger;
    }

    // Claim "id" do JWT emitido pelo AuthAPI (o usuário do token)
    private bool IsAuthenticatedAs(string userId) =>
        User.Identity?.IsAuthenticated == true &&
        !string.IsNullOrEmpty(userId) &&
        string.Equals(User.FindFirst("id")?.Value, userId, StringComparison.OrdinalIgnoreCase);

    private string? GetClaim(params string[] types) =>
        types.Select(t => User.FindFirst(t)?.Value).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private static Work EmptyWork(Guid memberId) => new()
    {
        Title = string.Empty,
        Company = string.Empty,
        Location = string.Empty,
        Registration = string.Empty,
        Description = string.Empty,
        Department = string.Empty,
        AdmissionDate = null, // preenchida pelo colaborador em Configurações > Trabalho
        MemberId = memberId,
    };

    /// <summary>
    /// Salva uma entidade nova; se outra requisição simultânea já criou o registro
    /// (violação de índice único), descarta a tentativa e devolve null para reler.
    /// </summary>
    private async Task<bool> TryCreateAsync(object entity)
    {
        try
        {
            await _uof.CommitAsync();
            return true;
        }
        catch (DbUpdateException ex)
        {
            _logger.LogInformation(ex, "Registro criado por requisição concorrente; reaproveitando o existente.");
            foreach (var entry in _db.ChangeTracker.Entries().Where(e => e.State == EntityState.Added).ToList())
            {
                entry.State = EntityState.Detached;
            }
            return false;
        }
    }

    /// <summary>
    /// Perfil completo (inclui endereço e telefone): apenas o próprio usuário ou Admin.
    /// Para ver o perfil de outro colaborador use GetMemberInfo.
    /// </summary>
    [HttpGet("GetProfileInfo")]
    public async Task<ActionResult> GetProfileInfo(string userId)
    {
        var isOwnUser = IsAuthenticatedAs(userId);
        if (!isOwnUser && !_ownership.IsAdmin(User)) return Forbid();

        try
        {
            var member = await _uof.MemberRepository.GetAsync(m => m.UserId == userId, includeProperties: "Address");

            // Usuários recém-cadastrados no AuthAPI ainda não têm Member/Profile:
            // cria os registros vazios na primeira consulta, apenas para o próprio usuário do token.
            if (member == null)
            {
                if (!isOwnUser) return NotFound("Nenhum membro encontrado.");

                var newMember = new Models.Member
                {
                    MemberId = Guid.NewGuid(),
                    UserId = userId,
                    Email = GetClaim(ClaimTypes.Email, "email"),
                    Name = GetClaim(ClaimTypes.Name, "unique_name") ?? GetClaim(ClaimTypes.Email, "email"),
                    dateOfMembership = DateTime.UtcNow,
                };
                _uof.MemberRepository.Create(newMember);

                if (await TryCreateAsync(newMember))
                {
                    _logger.LogInformation("Member criado automaticamente para o usuário {UserId}", userId);
                    member = newMember;
                }
                else
                {
                    // Índice único em Members.UserId: outra requisição criou primeiro
                    member = await _uof.MemberRepository.GetAsync(m => m.UserId == userId, includeProperties: "Address");
                    if (member == null) return StatusCode(500, "Não foi possível criar o membro.");
                }
            }

            var profile = await _uof.ProfileRepository.GetAsync(p => p.MemberId == member.MemberId);

            if (profile == null)
            {
                if (!isOwnUser) return NotFound("Profile não encontrado");

                // Profiles.WorkId é obrigatório no schema atual: reaproveita o Work do membro
                // ou cria um vazio junto (o EF insere o Work antes e preenche o WorkId).
                var existingWork = await _uof.WorkRepository.GetAsync(w => w.MemberId == member.MemberId);
                var newProfile = new UserProfile
                {
                    ProfileId = Guid.NewGuid(),
                    MemberId = member.MemberId,
                    Work = existingWork ?? EmptyWork(member.MemberId),
                };
                _uof.ProfileRepository.Create(newProfile);

                if (await TryCreateAsync(newProfile))
                {
                    _logger.LogInformation("Profile criado automaticamente para o member {MemberId}", member.MemberId);
                    profile = newProfile;
                }
                else
                {
                    profile = await _uof.ProfileRepository.GetAsync(p => p.MemberId == member.MemberId);
                    if (profile == null) return StatusCode(500, "Não foi possível criar o perfil.");
                }
            }

            var work = await _workService.GetWorkByMemberId(member.MemberId);

            var educations = await _educationService.GetEducationsByProfileId(profile.ProfileId);

            UserDto user = new()
            {
                Member = _mapper.Map<MemberDto>(member),
                Profile = _mapper.Map<UserProfileDto>(profile)
            };

            user.Profile.Work = work;
            user.Profile.Educations = educations;

            _response.Result = user;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao buscar perfil do usuário");
            _response.IsSuccess = false;
            _response.Message = ex.Message;
        }

        return Ok(_response);
    }

    /// <summary>
    /// Perfil público de um colaborador (sem endereço). Aberto também a visitantes, que não
    /// recebem dados pessoais (telefone, nascimento, gênero, matrícula e id de usuário).
    /// </summary>
    [AllowAnonymous]
    [HttpGet("GetMemberInfo")]
    public async Task<ActionResult> GetProfilebyMemberId(Guid memberId)
    {
        try
        {
            var member = await _uof.MemberRepository.GetAsync(m => m.MemberId == memberId, includeProperties: null);

            if (member == null) return NotFound("Nenhum membro encontrado.");

            var profile = await _uof.ProfileRepository.GetAsync(p => p.MemberId == member.MemberId);

            if (profile == null) return NotFound("Profile não encontrado");

            var work = await _workService.GetWorkByMemberId(member.MemberId);

            var educations = await _educationService.GetEducationsByProfileId(profile.ProfileId);

            UserDto user = new()
            {
                Member = _mapper.Map<MemberDto>(member),
                Profile = _mapper.Map<UserProfileDto>(profile)
            };

            user.Profile.Work = work;
            user.Profile.Educations = educations;

            // Endereço nunca faz parte do perfil público
            user.Member.Address = null;
            if (User.Identity?.IsAuthenticated != true)
            {
                user.Member.Phone = null;
                user.Member.UserId = null;
                user.Member.BirthDate = null;
                user.Profile.Gender = null;
                if (user.Profile.Work is not null) user.Profile.Work.Registration = string.Empty;
            }

            _response.Result = user;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao buscar perfil do usuário");
            _response.IsSuccess = false;
            _response.Message = ex.Message;
        }

        return Ok(_response);
    }

    /// <summary>Cria/atualiza o cadastro completo: apenas do próprio usuário (ou Admin).</summary>
    [HttpPost("UserUpsert")]
    public async Task<ActionResult> UserUpsert([FromBody] UserDto userDto)
    {
        if (userDto?.Member is null || userDto.Profile is null) return BadRequest("Dados inválidos.");
        if (!_ownership.IsUser(User, userDto.Member.UserId)) return Forbid();

        var response = new ResponseDto();

        try
        {
            // =========================
            // 1) MEMBER
            // =========================

            var member = await _uof.MemberRepository
                .GetAsync(m => m.UserId == userDto.Member.UserId);

            if (member == null)
            {
                member = _mapper.Map<Models.Member>(userDto.Member);
                member.MemberId = Guid.NewGuid();
                member.dateOfMembership = DateTime.UtcNow;

                _uof.MemberRepository.Create(member);
                await _uof.CommitAsync();
            }
            else
            {
                // Chave e vínculo com o usuário não são alterados pelo payload
                var memberId = member.MemberId;
                var userId = member.UserId;
                var joined = member.dateOfMembership;
                _mapper.Map(userDto.Member, member);
                member.MemberId = memberId;
                member.UserId = userId;
                member.dateOfMembership = joined;

                _uof.MemberRepository.Update(member);
                await _uof.CommitAsync();
            }

            // =========================
            // 2) PROFILE
            // =========================

            var profile = await _uof.ProfileRepository
                .GetAsync(p => p.MemberId == member.MemberId);

            if (profile == null)
            {
                profile = _mapper.Map<UserProfile>(userDto.Profile);
                profile.MemberId = member.MemberId;
                profile.ProfileId = Guid.NewGuid();
                profile.ProfileEducations = new List<Education>(); // gravadas no passo 4

                // Profiles.WorkId é obrigatório: usa o Work do membro, o enviado ou um vazio
                var existingWork = await _uof.WorkRepository.GetAsync(w => w.MemberId == member.MemberId);
                if (existingWork != null)
                {
                    profile.Work = existingWork;
                }
                else
                {
                    profile.Work ??= EmptyWork(member.MemberId);
                    profile.Work.WorkId = 0;
                    profile.Work.MemberId = member.MemberId;
                    profile.Work.AdmissionDate = profile.Work.AdmissionDate.HasValue
                        ? DateTime.SpecifyKind(profile.Work.AdmissionDate.Value.Date, DateTimeKind.Utc)
                        : null;
                }

                _uof.ProfileRepository.Create(profile);
                await _uof.CommitAsync();
            }
            else
            {
                var profileId = profile.ProfileId;
                _mapper.Map(userDto.Profile, profile);
                profile.ProfileId = profileId;
                profile.MemberId = member.MemberId;
                // Work e Educations são gravados pelos serviços próprios (passos 3 e 4);
                // não deixa o mapeamento criar entidades novas pela navegação
                profile.Work = null!;
                profile.ProfileEducations = null!;
                _uof.ProfileRepository.Update(profile);
                await _uof.CommitAsync();
            }

            // =========================
            // 3) WORK (1–1)
            // =========================
            if (userDto.Profile.Work != null)
            {
                await _workService.UpsertWorkAsync(
                    member.MemberId,
                    userDto.Profile.Work
                );
            }

            // =========================
            // 4) EDUCATIONS (1–N)
            // =========================
            if (userDto.Profile.Educations?.Any() == true)
            {
                await _educationService.UpsertEducationsAsync(
                    profile.ProfileId,
                    userDto.Profile.Educations
                );
            }


            response.Result = userDto;
            response.IsSuccess = true;
            response.Message = "Usuário salvo com sucesso";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao criar/atualizar usuário");
            response.IsSuccess = false;
            response.Message = ex.Message;
        }

        return Ok(response);
    }
}

using AutoMapper;
using Intranet.Services.IdentidadeAPI.Models;
using Intranet.Services.IdentidadeAPI.Models.DTOs;
using Intranet.Services.IdentidadeAPI.Repository.IRepository;
using Intranet.Services.IdentidadeAPI.Service.IService;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace Intranet.Services.IdentidadeAPI.Controllers;

[Route("api/[controller]")]
[ApiController]
public class UserController : ControllerBase
{
    private readonly IUnityOfWork _uof;
    private readonly IWorkService _workService;
    private readonly IEducationService _educationService;
    private readonly IMapper _mapper;
    private ResponseDto _response;
    private readonly ILogger<UserController> _logger;

    public UserController(IUnityOfWork unityOfWork, IWorkService workService, IEducationService educationService, IMapper mapper, ILogger<UserController> logger)
    {
        _uof = unityOfWork;
        _workService = workService;
        _educationService = educationService;
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

    [HttpGet("GetProfileInfo")]
    public async Task<ActionResult> GetProfileInfo(string userId)
    {
        try
        {
            var member = await _uof.MemberRepository.GetAsync(m => m.UserId == userId, includeProperties: "Address");

            // Usuários recém-cadastrados no AuthAPI ainda não têm Member/Profile:
            // cria os registros vazios na primeira consulta, apenas para o próprio usuário do token.
            var isOwnUser = IsAuthenticatedAs(userId);

            if (member == null)
            {
                if (!isOwnUser) return NotFound("Nenhum membro encontrado.");

                member = new Models.Member
                {
                    MemberId = Guid.NewGuid(),
                    UserId = userId,
                    Email = GetClaim(ClaimTypes.Email, "email"),
                    Name = GetClaim(ClaimTypes.Name, "unique_name") ?? GetClaim(ClaimTypes.Email, "email"),
                    dateOfMembership = DateTime.UtcNow,
                };
                _uof.MemberRepository.Create(member);
                await _uof.CommitAsync();
                _logger.LogInformation("Member criado automaticamente para o usuário {UserId}", userId);
            }

            var profile = await _uof.ProfileRepository.GetAsync(p => p.MemberId == member.MemberId);

            if (profile == null)
            {
                if (!isOwnUser) return NotFound("Profile não encontrado");

                // Profiles.WorkId é obrigatório no schema atual: cria um Work vazio junto
                // (o EF insere o Work antes e preenche o WorkId do Profile).
                profile = new UserProfile
                {
                    ProfileId = Guid.NewGuid(),
                    MemberId = member.MemberId,
                    Work = new Work
                    {
                        Title = string.Empty,
                        Company = string.Empty,
                        Location = string.Empty,
                        Registration = string.Empty,
                        Description = string.Empty,
                        Department = string.Empty,
                        AdmissionDate = DateTime.UtcNow,
                        MemberId = member.MemberId,
                    },
                };
                _uof.ProfileRepository.Create(profile);
                await _uof.CommitAsync();
                _logger.LogInformation("Profile criado automaticamente para o member {MemberId}", member.MemberId);
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

    [HttpPost("UserUpsert")]
    public async Task<ActionResult> UserUpsert([FromBody] UserDto userDto)
    {
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
                _mapper.Map(userDto.Member, member);
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
                _uof.ProfileRepository.Create(profile);
                await _uof.CommitAsync();
            }
            else
            {
                _mapper.Map(userDto.Profile, profile);
                profile.MemberId = member.MemberId;
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

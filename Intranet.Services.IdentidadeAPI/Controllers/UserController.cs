using AutoMapper;
using AutoMapper.Execution;
using Intranet.Services.IdentidadeAPI.Models;
using Intranet.Services.IdentidadeAPI.Models.DTOs;
using Intranet.Services.IdentidadeAPI.Repository.IRepository;
using Intranet.Services.IdentidadeAPI.Service.IService;
using Microsoft.AspNetCore.Mvc;

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

    [HttpGet("GetProfileInfo")]
    public async Task<ActionResult> GetProfileInfo(string userId)
    {
        try
        {
            var member = await _uof.MemberRepository.GetAsync(m => m.UserId == userId);

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
            var member = await _uof.MemberRepository
                .GetAsync(m => m.UserId == userDto.Member.UserId);

            if (member == null)
            {
                member = _mapper.Map<Models.Member>(userDto.Member);
                _uof.MemberRepository.Create(member);
            }
            else
            {
                _mapper.Map(userDto.Member, member);
                _uof.MemberRepository.Update(member);
            }

            await _uof.CommitAsync();

            var profile = await _uof.ProfileRepository
                .GetAsync(p => p.MemberId == member.MemberId);

            if (profile == null)
            {
                profile = _mapper.Map<UserProfile>(userDto.Profile);
                profile.MemberId = member.MemberId;
                _uof.ProfileRepository.Create(profile);
            }
            else
            {
                _mapper.Map(userDto.Profile, profile);
                _uof.ProfileRepository.Update(profile);
            }

            await _uof.CommitAsync();

            //if (userDto.Profile.Work != null)
            //{
            //    await _workService.UpsertWorkAsync(
            //        member.MemberId,
            //        userDto.Profile.Work
            //    );
            //}

            //if (userDto.Profile.Educations?.Any() == true)
            //{
            //    await _educationService.UpsertEducationsAsync(
            //        profile.ProfileId,
            //        userDto.Profile.Educations
            //    );
            //}

            response.Result = userDto;
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

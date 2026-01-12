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
    public async Task<ActionResult<ResponseDto>> GetProfileInfo(string userId)
    {
        try
        {
            var member = await _uof.MemberRepository.GetAsync(m => m.UserId == userId);

            if (member == null) return NotFound("Nenhum membro encontrado.");

            var profile = await _uof.ProfileRepository.GetAsync(p => p.MemberId == member.MemberId);

            if (profile == null) return NotFound("Profile não encontrado");

            var work = await _workService.GetWorkByProfileId(profile.ProfileId);

            var educatios = await _educationService.GetEducationsByProfileId(profile.ProfileId);

            UserDto user = new ()
            {
                Member = _mapper.Map<MemberDto>(member),
                Profile = _mapper.Map<UserProfileDto>(profile)
            };

            user.Profile.Work = work; ;
            user.Profile.Educations = educatios.ToList();

            _response.Result = user;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao buscar perfil do usuário");
            _response.IsSuccess = false;
            _response.Message = ex.Message;
        }
        
        return _response;
    }

    [HttpPost("UserUpsert")]
    public async Task<ResponseDto> UserUpsert([FromBody]UserDto userDto)
    {
        try
        {
            var memberFromDb = await _uof.MemberRepository.GetAsync(u => u.UserId == userDto.Member.UserId);
            if (memberFromDb == null)
            {
                memberFromDb = _mapper.Map<Models.Member>(userDto.Member);
                _uof.MemberRepository.Create(memberFromDb);
                await _uof.CommitAsync();

                userDto.Profile.MemberId = memberFromDb.MemberId;
                UserProfile profile = _mapper.Map<UserProfile>(userDto.Profile);
                _uof.ProfileRepository.Create(profile);
                await _uof.CommitAsync();
            }
            else
            {
                _mapper.Map(userDto.Member, memberFromDb);
                _uof.MemberRepository.Update(memberFromDb);
                await _uof.CommitAsync();

                userDto.Profile.MemberId = memberFromDb.MemberId;
                UserProfile profile = _mapper.Map<UserProfile>(userDto.Profile);
                _uof.ProfileRepository.Update(profile);
                await _uof.CommitAsync();
            }
                _response.Result = userDto;

        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao criar usuário");
            _response.IsSuccess = false;
            _response.Message = ex.Message;
        }
        return _response;
    }

}

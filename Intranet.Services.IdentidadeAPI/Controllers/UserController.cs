using AutoMapper;
using Intranet.Services.IdentidadeAPI.Models;
using Intranet.Services.IdentidadeAPI.Models.DTOs;
using Intranet.Services.IdentidadeAPI.Repository.IRepository;
using Intranet.Services.IdentidadeAPI.Service.IService;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.VisualBasic;

namespace Intranet.Services.IdentidadeAPI.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class UserController : ControllerBase
    {
        private readonly IUnityOfWork _uof;
        private readonly IWorkService _workService;
        private readonly IEducationService _educationService;
        private readonly IMapper _mapper;
        private readonly ILogger<UserController> _logger;

        public UserController(IUnityOfWork unityOfWork, IWorkService workService, IEducationService educationService, IMapper mapper, ILogger<UserController> logger)
        {
            _uof = unityOfWork;
            _workService = workService;
            _educationService = educationService;
            _mapper = mapper;
            _logger = logger;
        }

        [HttpGet("GetProfileInfo")]
        public async Task<ActionResult<UserDto>> GetProfileInfo(string userId)
        {
            try
            {
                var member = await _uof.MemberRepository.GetAsync(m => m.UserId == userId);

                if (member == null) return NotFound("Nenhum membro encontrado.");

                var profile = await _uof.ProfileRepository.GetAsync(p => p.MemberId == member.MemberId);

                if (profile == null) return NotFound("Profile não encontrado");

                var work = await _workService.GetWorkByProfileId(profile.ProfileId);

                var educatios = await _educationService.GetEducationsByProfileId(profile.ProfileId);

                var userDto = new UserDto
                {
                    Member = _mapper.Map<MemberDto>(member),
                    Profile = _mapper.Map<UserProfileDto>(profile)
                };

                userDto.Profile.Work = work; ;
                userDto.Profile.Educations = educatios.ToList();

                return Ok(userDto);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao buscar perfil do usuário");
                return StatusCode(500, "Erro interno");
            }
        }
    }
}

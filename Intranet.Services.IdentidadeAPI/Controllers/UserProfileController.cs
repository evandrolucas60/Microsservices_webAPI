using AutoMapper;
using Intranet.Services.IdentidadeAPI.Models;
using Intranet.Services.IdentidadeAPI.Models.DTOs;
using Intranet.Services.IdentidadeAPI.Repository.IRepository;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Intranet.Services.IdentidadeAPI.Controllers;

[Route("api/[controller]")]
[ApiController]
public class UserProfileController : ControllerBase
{
    private readonly IUnityOfWork _uof;
    private readonly ILogger<UserProfileController> _logger;
    private readonly IConfiguration _configuration;
    private ResponseDto _response;
    private IMapper _mapper;

    public UserProfileController(IUnityOfWork uof, ILogger<UserProfileController> logger, IMapper mapper, IConfiguration configuration)
    {
        _uof = uof;
        _logger = logger;
        _response = new ResponseDto();
        _mapper = mapper;
        _configuration = configuration;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<UserProfile>>> GetAllProfilesAsync()
    {
        try
        {
            var profilesList = await _uof.ProfileRepository.GetAllAsync();
            _response.Result = _mapper.Map<IEnumerable<UserProfileDto>>(profilesList);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Nenhum dado foi encontrado");
            _response.IsSuccess = false;
            _response.Message = ex.Message;
        }

        return Ok(_response);
    }

    [HttpGet("{profileId:Guid}")]
    public async Task<ActionResult<UserProfile>> GetUserProfileAsync(Guid profileId)
    {
        try
        {
            var profile = await _uof.ProfileRepository.GetAsync(
                p => p.ProfileId == profileId, includeProperties: null);

            _response.Result = _mapper.Map<UserProfileDto>(profile);

            if (profile is null)
            {
                _logger.LogWarning("Profile não Encotrado ou não existe");
                return NotFound("Objeto não existe...");
            }

        }
        catch (Exception ex)
        {
            _logger.LogWarning("Nenhum dado foi encontrado");
            _response.IsSuccess = false;
            _response.Message = ex.Message;
        }
        return Ok(_response);
    }

    [HttpGet("GetProfilebyMember/{memberId:Guid}")]
    public async Task<ActionResult<UserProfile>> GetProfilebyMemberAsync(Guid memberId)
    {
        try
        {
            var profile = await _uof.ProfileRepository.GetAsync(
            p => p.MemberId == memberId, includeProperties: null);

            _response.Result = _mapper.Map<UserProfileDto>(profile);

            if (profile is null)
            {
                _logger.LogWarning("Profile não Encotrado ou não existe");
                return NotFound("Objeto não existe...");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Nenhum dado foi encontrado");
            _response.IsSuccess = false;
            _response.Message = ex.Message;
        }
        return Ok(_response);
    }

    //[HttpPatch("{profileId:Guid}")]
    //[Consumes("multipart/form-data")]
    //public async Task<ActionResult> UpdatePartialProfile( Guid profileId,[FromForm] UserProfileDto dto)
    //{
    //    var photo = dto.ProfilePicture;

    //    var profile = await _uof.ProfileRepository.GetAsync(p => p.ProfileId == profileId);

    //    if (profile is null)
    //    {
    //        return BadRequest("Dados Inválidos");
    //    }

    //    if (photo == null || photo.Length == 0)
    //    {
    //        return BadRequest("Nehuma imagem enviada");
    //    }

    //    var extensionsGranted = new[] { ".jpg", ".png", ".jpeg" };
    //    var extension = Path.GetExtension(photo.FileName).ToLower();

    //    if (!extensionsGranted.Contains(extension))
    //    {
    //        return BadRequest("Formato de imagem inválido.");
    //    }

    //    try
    //    {
    //        var rootDirectory = _configuration["PathSettings:RootPath"];
    //        var folderpath = Path.Combine(rootDirectory, "profiles", profile.ProfileId.ToString());

    //        if (Directory.Exists(folderpath))
    //        {
    //            Directory.Delete(folderpath, true);
    //        }

    //        Directory.CreateDirectory(folderpath);


    //        var uniqueFileName = $"{Guid.NewGuid()}{extension}";
    //        var path = Path.Combine();

    //        using (var stream = new FileStream(path, FileMode.Create))
    //        {
    //            await photo.CopyToAsync(stream);
    //        }

    //        var rootDirectoryUrl = _configuration["PathSettings:RootPathURl"];
    //        profile.ProfilePicture = Path.Combine(rootDirectoryUrl, "profiles", profile.ProfileId.ToString(), uniqueFileName);

    //        _uof.ProfileRepository.Update(profile);
    //        await _uof.CommitAsync();
    //    }
    //    catch (Exception ex)
    //    {
    //        _logger.LogError(ex, "Erro log: {Message}", ex.Message);
    //        return StatusCode(500, new { mensagem = "Erro ao processar a imagem.", erro = ex.Message });
    //    }

    //    return Ok(new { profileImg = profile.ProfilePicture });
    //}

    [HttpPatch("{profileId:Guid}")]
    [Consumes("multipart/form-data")]
    public async Task<ActionResult> UpdatePartialProfile(
    Guid profileId,
    [FromForm] UpdatePartialUserProfileDto dto)
    {
        var profile = await _uof.ProfileRepository
            .GetAsync(p => p.ProfileId == profileId);

        if (profile is null)
            return BadRequest("Dados inválidos");

        try
        {
            var rootDirectory = _configuration["PathSettings:RootPath"];
            var rootDirectoryUrl = _configuration["PathSettings:RootPathURl"];

            // 🔵 Atualiza Gender
            if (!string.IsNullOrWhiteSpace(dto.Gender))
            {
                profile.Gender = dto.Gender;
            }

            // 🟢 Profile Picture
            if (dto.ProfilePicture != null && dto.ProfilePicture.Length > 0)
            {
                var folderpath = Path.Combine(
                rootDirectory,
                "profiles",
                profile.ProfileId.ToString(),"profileImage"
                );

                if (Directory.Exists(folderpath))
                {
                    Directory.Delete(folderpath, true);
                }

                Directory.CreateDirectory(folderpath);

                var extension = Path.GetExtension(dto.ProfilePicture.FileName).ToLower();
                var uniqueFileName = $"{Guid.NewGuid()}{extension}";
                var path = Path.Combine(folderpath, uniqueFileName);

                using var stream = new FileStream(path, FileMode.Create);
                await dto.ProfilePicture.CopyToAsync(stream);

                profile.ProfilePicture =
                    $"{rootDirectoryUrl}/profiles/{profile.ProfileId}/profileImage/{uniqueFileName}";
            }

            // 🟣 Cover Photo
            if (dto.CoverPhoto != null && dto.CoverPhoto.Length > 0)
            {
                var folderpath = Path.Combine(
                rootDirectory,
                "profiles",
                profile.ProfileId.ToString(), "coverImage"
                );

                if (Directory.Exists(folderpath))
                {
                    Directory.Delete(folderpath, true);
                }

                Directory.CreateDirectory(folderpath);

                var extension = Path.GetExtension(dto.CoverPhoto.FileName).ToLower();
                var uniqueFileName = $"{Guid.NewGuid()}{extension}";
                var path = Path.Combine(folderpath, uniqueFileName);

                using var stream = new FileStream(path, FileMode.Create);
                await dto.CoverPhoto.CopyToAsync(stream);

                profile.CoverPhoto =
                    $"{rootDirectoryUrl}/profiles/{profile.ProfileId}/coverImage/{uniqueFileName}";
            }

            _uof.ProfileRepository.Update(profile);
            await _uof.CommitAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro: {Message}", ex.Message);

            return StatusCode(500, new
            {
                mensagem = "Erro ao atualizar perfil",
                erro = ex.Message
            });
        }

        return Ok(profile);
    }
}
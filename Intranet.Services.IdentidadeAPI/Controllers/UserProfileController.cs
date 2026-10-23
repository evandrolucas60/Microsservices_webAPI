using AutoMapper;
using Intranet.Services.IdentidadeAPI.Models;
using Intranet.Services.IdentidadeAPI.Models.DTOs;
using Intranet.Services.IdentidadeAPI.Repository.IRepository;
using Intranet.Services.IdentidadeAPI.Utility;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Intranet.Services.IdentidadeAPI.Controllers;

[Route("api/[controller]")]
[ApiController]
[Authorize]
public class UserProfileController : ControllerBase
{
    // Apenas imagens: o arquivo é servido publicamente pela URL gravada no perfil
    private static readonly HashSet<string> AllowedImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp",
    };
    private const long MaxImageBytes = 5 * 1024 * 1024;

    private readonly IUnityOfWork _uof;
    private readonly ILogger<UserProfileController> _logger;
    private readonly IConfiguration _configuration;
    private readonly IOwnershipChecker _ownership;
    private ResponseDto _response;
    private IMapper _mapper;

    public UserProfileController(IUnityOfWork uof, ILogger<UserProfileController> logger, IMapper mapper, IConfiguration configuration, IOwnershipChecker ownership)
    {
        _uof = uof;
        _logger = logger;
        _response = new ResponseDto();
        _mapper = mapper;
        _configuration = configuration;
        _ownership = ownership;
    }

    private static string? ValidateImage(IFormFile? file)
    {
        if (file is null || file.Length == 0) return null;
        if (file.Length > MaxImageBytes) return "A imagem deve ter no máximo 5 MB.";
        if (!AllowedImageExtensions.Contains(Path.GetExtension(file.FileName))) return "Envie uma imagem JPG, PNG, GIF ou WEBP.";
        if (!file.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return "O arquivo enviado não é uma imagem.";
        return null;
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
        if (!await _ownership.OwnsProfileAsync(User, profileId)) return Forbid();

        var imageError = ValidateImage(dto.ProfilePicture) ?? ValidateImage(dto.CoverPhoto);
        // Respostas em JSON (ResponseDto) para o front exibir a mensagem ao usuário
        if (imageError is not null) return BadRequest(new ResponseDto { IsSuccess = false, Message = imageError });

        var profile = await _uof.ProfileRepository
            .GetAsync(p => p.ProfileId == profileId);

        if (profile is null)
            return BadRequest(new ResponseDto { IsSuccess = false, Message = "Perfil não encontrado." });

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

            // Detalhes da exceção ficam só no log
            return StatusCode(500, new ResponseDto { IsSuccess = false, Message = "Não foi possível atualizar o perfil." });
        }

        return Ok(profile);
    }

    /// <summary>
    /// Atualiza os links de redes sociais do perfil do usuário autenticado (claim "id").
    /// Campo vazio remove o link; os demais precisam ser URLs http/https da rede correspondente.
    /// </summary>
    [HttpPut("redes-sociais")]
    public async Task<ActionResult> UpdateSocialLinks([FromBody] SocialLinksDto dto)
    {
        var userId = _ownership.UserId(User);
        if (string.IsNullOrEmpty(userId)) return Unauthorized();
        if (dto is null) return BadRequest(new ResponseDto { IsSuccess = false, Message = "Dados inválidos." });

        var erros = new List<string>();
        string? Validar(string? valor, string rede, string[] hosts)
        {
            if (!SocialLinkValidator.TryNormalize(valor, rede, hosts, out var normalizado, out var erro))
            {
                erros.Add(erro!);
            }
            return normalizado;
        }

        var twitter = Validar(dto.TwitterUrl, "X (Twitter)", SocialLinkValidator.TwitterHosts);
        var facebook = Validar(dto.FacebookUrl, "Facebook", SocialLinkValidator.FacebookHosts);
        var instagram = Validar(dto.InstagramUrl, "Instagram", SocialLinkValidator.InstagramHosts);
        var linkedin = Validar(dto.LinkedinUrl, "LinkedIn", SocialLinkValidator.LinkedinHosts);

        if (erros.Count > 0)
        {
            return BadRequest(new ResponseDto { IsSuccess = false, Message = string.Join(" ", erros) });
        }

        var member = await _uof.MemberRepository.GetAsync(m => m.UserId == userId);
        if (member is null) return NotFound(new ResponseDto { IsSuccess = false, Message = "Membro não encontrado." });

        var profile = await _uof.ProfileRepository.GetAsync(p => p.MemberId == member.MemberId);
        if (profile is null) return NotFound(new ResponseDto { IsSuccess = false, Message = "Perfil não encontrado." });

        try
        {
            profile.TwitterUrl = twitter;
            profile.FacebookUrl = facebook;
            profile.InstagramUrl = instagram;
            profile.LinkedinUrl = linkedin;

            _uof.ProfileRepository.Update(profile);
            await _uof.CommitAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao atualizar redes sociais do perfil {ProfileId}", profile.ProfileId);
            return StatusCode(500, new ResponseDto { IsSuccess = false, Message = "Não foi possível salvar as redes sociais." });
        }

        return Ok(new ResponseDto
        {
            Result = new SocialLinksDto
            {
                TwitterUrl = twitter,
                FacebookUrl = facebook,
                InstagramUrl = instagram,
                LinkedinUrl = linkedin,
            },
            Message = "Redes sociais atualizadas.",
        });
    }
}
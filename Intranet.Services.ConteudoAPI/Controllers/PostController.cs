using AutoMapper;
using Intranet.Services.ConteudoAPI.Models;
using Intranet.Services.ConteudoAPI.Models.DTOs;
using Intranet.Services.ConteudoAPI.Repository.IRepository;
using Microsoft.AspNetCore.Mvc;

namespace Intranet.Services.ConteudoAPI.Controllers;

[Route("api/[controller]")]
[ApiController]
public class PostController : ControllerBase
{
    private readonly IUnityOfWork _uof;
    private readonly ILogger<PostController> _logger;
    private ResponseDto _response;
    private IMapper _mapper;

    public PostController(IUnityOfWork uof, ILogger<PostController> logger, IMapper mapper)
    {
        _uof = uof;
        _logger = logger;
        _response = new ResponseDto();
        _mapper = mapper;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<Post>>> GetAsync()
    {
        try
        {
            var objlist = await _uof.PostRepository.GetAllAsync();
            _response.Result = _mapper.Map<IEnumerable<PostDto>>(objlist);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Nenhum dado foi encontrado");
            _response.IsSuccess = false;
            _response.Message = ex.Message;
        }
        return Ok(_response);
    }

    [HttpGet("{postId:int}")]
    public async Task<ActionResult<Post>> GetAsync(int postId)
    {
        try
        {
            var obj = await _uof.PostRepository.GetAsync(
            p => p.PostId == postId,
            includeProperties: null
        );

            _response.Result = _mapper.Map<PostDto>(obj);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Nenhum dado foi encontrado");
            _response.IsSuccess = false;
            _response.Message = ex.Message;
        }
        return Ok(_response);
    }

    [HttpGet("GetPostbyMember/{id:int}")]
    public async Task<ActionResult<Post>> GetPostbyMember(int id)
    {
        try
        {
            var obj = await _uof.PostRepository.GetAsync(c => c.MemberId == id);
            _response.Result = _mapper.Map<PostDto>(obj);

            if (obj is null)
            {
                _logger.LogWarning("Objeto não Encontrado ou não existe");
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

    [HttpGet("GetAllPostByMemberId/{id:int}")]
    public async Task<ActionResult<Post>> GetAllPostByMemberId(int id)
    {
        try
        {
            var list = await _uof.PostRepository.GetAllAsync(c => c.MemberId == id);
            var postsDto = _mapper.Map<List<PostDto>>(list);

            foreach (var postDto in postsDto)
            {
                postDto.TotalLikes = await _uof.PostRepository.CountAsync(
                    l => l.PostId == postDto.PostId
                    );
            }
            _response.Result = postsDto;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Nenhum dado foi encontrado");
            _response.IsSuccess = false;
            _response.Message = ex.Message;
        }
        return Ok(_response);
    }

    [HttpPost]
    public async Task<ActionResult> Post([FromBody] PostDto postDto)
    {
        try
        {
            Post obj = _mapper.Map<Post>(postDto);
            var categoriaCriada = _uof.PostRepository.Create(obj);
            await _uof.CommitAsync();

            _response.Result = _mapper.Map<PostDto>(obj);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Dados Inválidos");
            _response.IsSuccess = false;
            _response.Message = ex.Message;
        }
        return Ok(_response);
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult> Put(int id, PostDto postDto)
    {
        try
        {
            Post obj = _mapper.Map<Post>(postDto);
            _uof.PostRepository.Update(obj);
            await _uof.CommitAsync();

            if (id != obj.PostId)
            {
                _logger.LogWarning("Dados inválidos");
                return BadRequest("Dados inválidos");
            }

            _response.Result = _mapper.Map<PostDto>(obj);
        }
        catch (Exception ex)
        {
            _response.IsSuccess = false;
            _response.Message = ex.Message;
        }
        return Ok(_response);
    }

    [HttpPatch("UpdatePartial/{postId:int}")]
    public async Task<ActionResult> PatchMemberAsync(int postId, [FromBody] PostDto postDto)
    {
        try
        {
            var post = await _uof.PostRepository.GetAsync(
                m => m.PostId == postId,
                includeProperties: null
            );

            if (post == null)
                return NotFound("Membro não encontrado");

            // 🔹 Patch parcial (somente campos enviados)
            if (!string.IsNullOrWhiteSpace(postDto.Text))
                post.Text = postDto.Text;

            _uof.PostRepository.Update(post);
            await _uof.CommitAsync();

            _response.Result = _mapper.Map<PostDto>(post);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao aplicar PATCH no memberId {memberId}", postId);
            _response.IsSuccess = false;
            _response.Message = ex.Message;
        }

        return Ok(_response);
    }

    [HttpPatch("ToggleLikeAsync/{postId:int}")]
    public async Task<ActionResult> ToggleLikeAsync(
    int postId,
    [FromBody] CreateLikeDto dto)
    {
        try
        {
            var existingLike = await _uof.LikeRepository.GetAsync(
                l => l.PostId == postId && l.MemberId == dto.MemberId
            );

            if (existingLike == null)
            {
                _uof.LikeRepository.Create(new Like
                {
                    PostId = postId,
                    MemberId = dto.MemberId
                });
            }
            else
            {
                _uof.LikeRepository.Delete(existingLike);
            }

            await _uof.CommitAsync();

            var totalLikes = await _uof.LikeRepository.CountAsync(
                l => l.PostId == postId
            );

            var post = await _uof.PostRepository.GetAsync(
                p => p.PostId == postId
            );

            if (post == null)
                return NotFound();

            var postDto = _mapper.Map<PostDto>(post);
            postDto.TotalLikes = totalLikes;

            return Ok(postDto);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao curtir post {PostId}", postId);
            return StatusCode(500, "Erro interno ao processar like.");
        }
    }



    [HttpDelete("{id:int}")]
    public async Task<ActionResult> Delete(int id)
    {
        try
        {
            Post obj = await _uof.PostRepository.GetAsync(c => c.PostId == id);


            if (obj is null)
            {
                _logger.LogWarning($"Objeto não Encontrado ou não existe");
                return NotFound("Objeto não existe...");
            }

            var objExcluido = _uof.PostRepository.Delete(obj);
            await _uof.CommitAsync();
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Erro interno ao tentar deletar: {ex.Message}");
        }

        return Ok(_response);
    }

}

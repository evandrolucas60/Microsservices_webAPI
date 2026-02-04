using AutoMapper;
using Intranet.Services.ConteudoAPI.Models;
using Intranet.Services.ConteudoAPI.Models.DTOs;
using Intranet.Services.ConteudoAPI.Repository.IRepository;
using Microsoft.AspNetCore.Mvc;

namespace Intranet.Services.ConteudoAPI.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class CommentController : ControllerBase
    {
        private readonly IUnityOfWork _uof;
        private readonly ILogger<CommentController> _logger;
        private ResponseDto _response;
        private IMapper _mapper;

        public CommentController(IUnityOfWork uof, ILogger<CommentController> logger, IMapper mapper)
        {
            _uof = uof;
            _logger = logger;
            _response = new ResponseDto();
            _mapper = mapper;
        }

        [HttpGet]
        public async Task<ActionResult<IEnumerable<Comment>>> GetAsync()
        {
            try
            {
                var objlist = await _uof.CommentRepository.GetAllAsync();
                _response.Result = _mapper.Map<IEnumerable<CommentDto>>(objlist);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Nenhum dado foi encontrado");
                _response.IsSuccess = false;
                _response.Message = ex.Message;
            }
            return Ok(_response);
        }

        [HttpGet("GetByPostId/{id:int}")]
        public async Task<ActionResult<Comment>> GetByPostIdAsync(int id)
        {
            try
            {
                var list = await _uof.CommentRepository.GetAllAsync(c => c.PostId == id);
                _response.Result = _mapper.Map<List<CommentDto>>(list);
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
        public async Task<ActionResult<Comment>> GetByMemberAsync(int id)
        {
            try
            {
                var obj = await _uof.CommentRepository.GetAsync(c => c.MemberId == id);
                _response.Result = _mapper.Map<CommentDto>(obj);

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

        [HttpPost]
        public async Task<ActionResult> Post([FromBody] CommentDto commentDto)
        {
            try
            {
                Comment obj = _mapper.Map<Comment>(commentDto);
                var categoriaCriada = _uof.CommentRepository.Create(obj);
                await _uof.CommitAsync();

                _response.Result = _mapper.Map<CommentDto>(obj);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Dados Inválidos");
                _response.IsSuccess = false;
                _response.Message = ex.Message;
            }
            return Ok(_response);
        }

        [HttpGet("GetAllCommentByMemberId/{postId:int}")]
        public async Task<ActionResult<List<CommentDto>>> GetAllCommentByMemberId(int postId)
        {
            try
            {
                var comments = await _uof.CommentRepository.GetAllByMemberIdWithLikesAsync(postId);
                _response.Result = comments;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao buscar posts do membro {MemberId}", postId);
                _response.IsSuccess = false;
                _response.Message = "Erro ao buscar posts.";
            }

            return Ok(_response);
        }

        [HttpPut("{id:int}")]
        public async Task<ActionResult> Put(int id, CommentDto commentDto)
        {
            try
            {
                Comment obj = _mapper.Map<Comment>(commentDto);
                _uof.CommentRepository.Update(obj);
                await _uof.CommitAsync();

                if (id != obj.CommentId)
                {
                    _logger.LogWarning("Dados inválidos");
                    return BadRequest("Dados inválidos");
                }

                _response.Result = _mapper.Map<CommentDto>(obj);
            }
            catch (Exception ex)
            {
                _response.IsSuccess = false;
                _response.Message = ex.Message;
            }
            return Ok(_response);
        }

        [HttpPatch("UpdatePartial/{commentId:int}")]
        public async Task<ActionResult> PatchMemberAsync(int commentId, [FromBody] CommentDto commentDto)
        {
            try
            {
                var comment = await _uof.CommentRepository.GetAsync(
                    m => m.PostId == commentId,
                    includeProperties: null
                );

                if (comment == null)
                    return NotFound("Membro não encontrado");

                // 🔹 Patch parcial (somente campos enviados)
                if (!string.IsNullOrWhiteSpace(commentDto.Text))
                    comment.Text = commentDto.Text;

                _uof.CommentRepository.Update(comment);
                await _uof.CommitAsync();

                _response.Result = _mapper.Map<CommentDto>(comment);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao aplicar PATCH no memberId {memberId}", commentId);
                _response.IsSuccess = false;
                _response.Message = ex.Message;
            }

            return Ok(_response);
        }

        [HttpPatch("ToggleLikeAsync/{commentId:int}")]
        public async Task<ActionResult> ToggleLikeAsync(
            int commentId,
            [FromBody] CreateLikeDto dto)
        {
            try
            {
                var comment = await _uof.CommentRepository.GetAsync(
                    c => c.CommentId == commentId
                );

                if (comment == null)
                    return NotFound("Comentário não encontrado.");

                var existingLike = await _uof.CommentLikeRepository.GetAsync(
                    l => l.CommentId == commentId && l.MemberId == dto.MemberId
                );

                if (existingLike == null)
                {
                    _uof.CommentLikeRepository.Create(new CommentLike
                    {
                        CommentId = commentId,
                        MemberId = dto.MemberId
                    });
                }
                else
                {
                    _uof.CommentLikeRepository.Delete(existingLike);
                }

                await _uof.CommitAsync();

                var totalLikes = await _uof.CommentLikeRepository.CountAsync(
                    l => l.CommentId == commentId
                );

                var commentDto = _mapper.Map<CommentDto>(comment);
                commentDto.TotalLikes = totalLikes;

                return Ok(commentDto);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao curtir comentário {CommentId}", commentId);
                return StatusCode(500, "Erro interno ao processar like.");
            }
        }

        [HttpDelete("{id:int}")]
        public async Task<ActionResult> Delete(int id)
        {
            try
            {
                Comment obj = await _uof.CommentRepository.GetAsync(c => c.CommentId == id);


                if (obj is null)
                {
                    _logger.LogWarning($"Objeto não Encontrado ou não existe");
                    return NotFound("Objeto não existe...");
                }

                var objExcluido = _uof.CommentRepository.Delete(obj);
                await _uof.CommitAsync();
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Erro interno ao tentar deletar: {ex.Message}");
            }

            return Ok(_response);
        }
    }
}

using AutoMapper;
using Intranet.Services.ConteudoAPI.Models;
using Intranet.Services.ConteudoAPI.Models.DTOs;


namespace Intranet.Services.ConteudoAPI;

public class MappingConfig : Profile
{
    public MappingConfig()
    {
        CreateMap<Comment, CommentDto>().ReverseMap();
        CreateMap<Post, PostDto>().ReverseMap();

        CreateMap<PostLike, CreateLikeDto>();
        CreateMap<CommentLike, CreateLikeDto>();
    }
}

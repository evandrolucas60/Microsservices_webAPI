using AutoMapper;
using Intranet.Services.ConteudoAPI.Models;
using Intranet.Services.ConteudoAPI.Models.DTOs;


namespace Intranet.Services.ConteudoAPI;

public class MappingConfig
{
    public static MapperConfiguration RegisterMaps()
    {
        var mappingConfig = new MapperConfiguration(config =>
        {
            config.CreateMap<Comment, CommentDto>().ReverseMap();

            config.CreateMap<Post, PostDto>().ReverseMap();

            config.CreateMap<PostLike, CreateLikeDto>();
            config.CreateMap<CommentLike, CreateLikeDto>();
        });
        return mappingConfig;
    }
}

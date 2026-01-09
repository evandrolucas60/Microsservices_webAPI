using AutoMapper;
using Intranet.Services.IdentidadeAPI.Models;
using Intranet.Services.IdentidadeAPI.Models.DTOs;

namespace Intranet.Services.IdentidadeAPI;

public class MappingConfig
{
    public static MapperConfiguration RegisterMaps()
    {
        var mappingConfig = new MapperConfiguration(config =>
        {
            config.CreateMap<Education, EducationDto>().ReverseMap();
            config.CreateMap<Member, MemberDto>().ReverseMap();
            config.CreateMap<UserProfile, UserProfileDto>().ReverseMap();
            config.CreateMap<Work, WorkDto>().ReverseMap();
        });
        return mappingConfig;
    }
}

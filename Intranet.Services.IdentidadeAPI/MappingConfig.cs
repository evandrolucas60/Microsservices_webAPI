using AutoMapper;
using Intranet.Services.IdentidadeAPI.Models;
using Intranet.Services.IdentidadeAPI.Models.DTOs;

namespace Intranet.Services.IdentidadeAPI;

public class MappingConfig : Profile
{
    public MappingConfig()
    {
        CreateMap<Education, EducationDto>().ReverseMap();
        CreateMap<Member, MemberDto>().ReverseMap();
        CreateMap<UserProfile, UserProfileDto>().ReverseMap();
        CreateMap<Work, WorkDto>().ReverseMap();
        CreateMap<Address, AddressDto>().ReverseMap();
    }
}


using AutoMapper;
using Intranet.Services.IdentidadeAPI.Models;
using Intranet.Services.IdentidadeAPI.Models.DTOs;

namespace Intranet.Services.IdentidadeAPI;

public class MappingConfig : Profile
{
    public MappingConfig()
    {
        CreateMap<Education, EducationDto>().ReverseMap();
        // DTO -> entidade não toca no endereço: MemberDto.Address é a entidade Address e,
        // mapeada por referência, permitiria reatribuir o endereço de outro membro.
        // Endereço é gravado apenas por api/Address (com checagem de dono).
        CreateMap<Member, MemberDto>().ReverseMap()
            .ForMember(d => d.Address, o => o.Ignore());

        // DTO -> entidade não cria Work pela navegação: o trabalho é gravado por api/Work
        CreateMap<UserProfile, UserProfileDto>().ReverseMap()
            .ForMember(d => d.Work, o => o.Ignore());
        CreateMap<Work, WorkDto>().ReverseMap();
        CreateMap<Address, AddressDto>().ReverseMap();
    }
}


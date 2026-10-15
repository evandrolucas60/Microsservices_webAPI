using Intranet.Services.IdentidadeAPI.Models.DTOs;

namespace Intranet.Services.IdentidadeAPI.Service.IService
{
    /// <summary>
    /// Hierarquia de gestores (gestor direto de cada colaborador): consulta, alteração com
    /// validação de ciclos e organograma por setor. Usado pelo HierarquiaController e pelos
    /// consumers de mensageria (aprovações em outros serviços).
    /// </summary>
    public interface IHierarquiaService
    {
        /// <summary>Máximo de níveis percorridos acima de um colaborador.</summary>
        int MaxProfundidade { get; }

        /// <summary>Gestores do membro, do direto até o topo (limitado a MaxProfundidade).</summary>
        Task<(List<Guid> Ids, bool Incompleta)> ObterCadeiaIdsAsync(Guid memberId, CancellationToken ct = default);

        /// <summary>Gestor direto do membro; null se não tiver. Lança NotFoundException se o membro não existir.</summary>
        Task<Guid?> ObterGestorDiretoAsync(Guid memberId, CancellationToken ct = default);

        Task<bool> ExisteAsync(Guid memberId, CancellationToken ct = default);

        /// <summary>Resumo (nome, cargo, setor, foto, gestor) dos membros informados.</summary>
        Task<Dictionary<Guid, MembroHierarquiaDto>> ObterResumosAsync(IEnumerable<Guid> memberIds, CancellationToken ct = default);

        Task<HierarquiaMembroDto> ObterHierarquiaAsync(Guid memberId, CancellationToken ct = default);

        /// <summary>Define (ou remove, com null) o gestor direto. Valida existência, autogestão e ciclos.</summary>
        Task<HierarquiaMembroDto> AlterarGestorAsync(Guid memberId, Guid? gestorMemberId, CancellationToken ct = default);

        /// <summary>Colaboradores do setor (vazio = empresa toda) mais os gestores acima deles, mesmo de outros setores.</summary>
        Task<OrganogramaDto> ObterOrganogramaAsync(string? departamento, CancellationToken ct = default);
    }
}

using BuildingBlocks.Shared.Exceptions;
using Intranet.Services.IdentidadeAPI.Data;
using Intranet.Services.IdentidadeAPI.Models.DTOs;
using Intranet.Services.IdentidadeAPI.Service.IService;
using Microsoft.EntityFrameworkCore;

namespace Intranet.Services.IdentidadeAPI.Service
{
    public class HierarquiaService : IHierarquiaService
    {
        // Limites: níveis acima de um colaborador, liderados listados e colaboradores no organograma
        private const int Profundidade = 20;
        private const int MaxLiderados = 500;
        private const int MaxOrganograma = 2000;

        // Chave do advisory lock do PostgreSQL que serializa as trocas de gestor:
        // duas trocas simultâneas (A -> B e B -> A) não podem, juntas, formar um ciclo
        private const long LockAlteracaoGestor = 7_310_250_901;

        private readonly AppDbContext _db;
        private readonly ILogger<HierarquiaService> _logger;

        public HierarquiaService(AppDbContext db, ILogger<HierarquiaService> logger)
        {
            _db = db;
            _logger = logger;
        }

        public int MaxProfundidade => Profundidade;

        public Task<bool> ExisteAsync(Guid memberId, CancellationToken ct = default) =>
            _db.Members.AsNoTracking().AnyAsync(m => m.MemberId == memberId, ct);

        public async Task<Guid?> ObterGestorDiretoAsync(Guid memberId, CancellationToken ct = default)
        {
            var linha = await _db.Members.AsNoTracking()
                .Where(m => m.MemberId == memberId)
                .Select(m => new { m.GestorMemberId })
                .FirstOrDefaultAsync(ct);

            if (linha is null) throw new NotFoundException("Colaborador não encontrado.");
            return linha.GestorMemberId;
        }

        /// <summary>
        /// Sobe a hierarquia a partir de "inicio" (incluído). Para em null, ao repetir um membro
        /// (ciclo antigo nos dados) ou ao atingir o limite; Incompleta indica que ainda havia gestor acima.
        /// </summary>
        private async Task<(List<Guid> Ids, bool Incompleta)> SubirAsync(Guid? inicio, int limite, CancellationToken ct)
        {
            var ids = new List<Guid>();
            var visitados = new HashSet<Guid>();
            var atual = inicio;

            while (atual is Guid id)
            {
                if (!visitados.Add(id)) return (ids, true);
                if (ids.Count >= limite) return (ids, true);
                ids.Add(id);

                // Busca pela chave primária: normalmente poucos níveis
                atual = await _db.Members.AsNoTracking()
                    .Where(m => m.MemberId == id)
                    .Select(m => m.GestorMemberId)
                    .FirstOrDefaultAsync(ct);
            }

            return (ids, false);
        }

        public async Task<(List<Guid> Ids, bool Incompleta)> ObterCadeiaIdsAsync(Guid memberId, CancellationToken ct = default)
        {
            var gestor = await ObterGestorDiretoAsync(memberId, ct);
            var (ids, incompleta) = await SubirAsync(gestor, Profundidade, ct);
            // O próprio membro nunca aparece na sua cadeia (só se houver ciclo antigo nos dados)
            var corte = ids.IndexOf(memberId);
            if (corte >= 0)
            {
                ids = ids.Take(corte).ToList();
                incompleta = true;
            }
            return (ids, incompleta);
        }

        // Member LEFT JOIN Work LEFT JOIN Profile, projetado no resumo da hierarquia
        private IQueryable<OrganogramaNoDto> Resumos() =>
            from m in _db.Members.AsNoTracking()
            join w in _db.Works.AsNoTracking() on m.MemberId equals w.MemberId into works
            from w in works.DefaultIfEmpty()
            join p in _db.Profiles.AsNoTracking() on m.MemberId equals p.MemberId into profiles
            from p in profiles.DefaultIfEmpty()
            select new OrganogramaNoDto
            {
                MemberId = m.MemberId,
                Nome = m.Name,
                Cargo = w != null ? w.Title : null,
                Departamento = w != null ? w.Department : null,
                Foto = p != null ? p.ProfilePicture : null,
                GestorMemberId = m.GestorMemberId,
            };

        private static MembroHierarquiaDto ParaResumo(MembroHierarquiaDto n) => new()
        {
            MemberId = n.MemberId,
            Nome = n.Nome,
            Cargo = n.Cargo,
            Departamento = n.Departamento,
            Foto = n.Foto,
            GestorMemberId = n.GestorMemberId,
        };

        public async Task<Dictionary<Guid, MembroHierarquiaDto>> ObterResumosAsync(IEnumerable<Guid> memberIds, CancellationToken ct = default)
        {
            var ids = memberIds.Where(id => id != Guid.Empty).Distinct().ToList();
            if (ids.Count == 0) return new Dictionary<Guid, MembroHierarquiaDto>();

            var linhas = await Resumos().Where(r => ids.Contains(r.MemberId)).ToListAsync(ct);
            return linhas
                .GroupBy(r => r.MemberId)
                .ToDictionary(g => g.Key, g => ParaResumo(g.First()));
        }

        public async Task<HierarquiaMembroDto> ObterHierarquiaAsync(Guid memberId, CancellationToken ct = default)
        {
            var (cadeiaIds, incompleta) = await ObterCadeiaIdsAsync(memberId, ct);

            var lideradosQuery = _db.Members.AsNoTracking().Where(m => m.GestorMemberId == memberId && m.MemberId != memberId);
            var totalLiderados = await lideradosQuery.CountAsync(ct);
            var lideradosIds = await lideradosQuery
                .OrderBy(m => m.Name)
                .Select(m => m.MemberId)
                .Take(MaxLiderados)
                .ToListAsync(ct);

            var resumos = await ObterResumosAsync(cadeiaIds.Concat(lideradosIds).Append(memberId), ct);
            if (!resumos.TryGetValue(memberId, out var membro)) throw new NotFoundException("Colaborador não encontrado.");

            var cadeia = cadeiaIds.Where(resumos.ContainsKey).Select(id => resumos[id]).ToList();

            return new HierarquiaMembroDto
            {
                Membro = membro,
                Gestor = cadeia.FirstOrDefault(),
                Cadeia = cadeia,
                CadeiaIncompleta = incompleta,
                TotalCadeia = cadeia.Count,
                Liderados = lideradosIds
                    .Where(resumos.ContainsKey)
                    .Select(id => resumos[id])
                    .OrderBy(l => l.Nome, StringComparer.CurrentCultureIgnoreCase)
                    .ToList(),
                TotalLiderados = totalLiderados,
            };
        }

        public async Task<HierarquiaMembroDto> AlterarGestorAsync(Guid memberId, Guid? gestorMemberId, CancellationToken ct = default)
        {
            if (gestorMemberId == Guid.Empty) gestorMemberId = null;

            await using (var transacao = await _db.Database.BeginTransactionAsync(ct))
            {
                // Serializa as alterações de gestor até o commit (liberado automaticamente no fim da transação)
                await _db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({LockAlteracaoGestor})", ct);

                var membro = await _db.Members.FirstOrDefaultAsync(m => m.MemberId == memberId, ct)
                    ?? throw new NotFoundException("Colaborador não encontrado.");

                if (gestorMemberId is Guid gestorId)
                {
                    if (gestorId == memberId)
                        throw new BusinessRuleException("Um colaborador não pode ser gestor de si mesmo.");

                    if (!await ExisteAsync(gestorId, ct))
                        throw new NotFoundException("Gestor não encontrado.");

                    // A cadeia do novo gestor (ele incluído) vira a cadeia do membro:
                    // não pode passar pelo próprio membro (ciclo) nem passar do limite de níveis
                    var (cadeiaGestor, incompleta) = await SubirAsync(gestorId, Profundidade, ct);
                    if (cadeiaGestor.Contains(memberId))
                        throw new BusinessRuleException(
                            "Não é possível: o gestor escolhido já está abaixo deste colaborador na hierarquia (formaria um ciclo).");
                    if (incompleta)
                        throw new BusinessRuleException(
                            $"Não é possível: a hierarquia passaria de {Profundidade} níveis acima deste colaborador.");
                }

                if (membro.GestorMemberId != gestorMemberId)
                {
                    var anterior = membro.GestorMemberId;
                    membro.GestorMemberId = gestorMemberId;
                    await _db.SaveChangesAsync(ct);
                    _logger.LogInformation(
                        "Gestor do membro {MemberId} alterado de {Anterior} para {Novo}", memberId, anterior, gestorMemberId);
                }

                await transacao.CommitAsync(ct);
            }

            return await ObterHierarquiaAsync(memberId, ct);
        }

        public async Task<OrganogramaDto> ObterOrganogramaAsync(string? departamento, CancellationToken ct = default)
        {
            var setor = string.IsNullOrWhiteSpace(departamento) ? null : departamento.Trim();

            var query = Resumos();
            if (setor is not null) query = query.Where(r => r.Departamento == setor);

            var linhas = await query
                .OrderBy(r => r.Nome)
                .Take(MaxOrganograma + 1)
                .ToListAsync(ct);

            var truncado = linhas.Count > MaxOrganograma;
            var nos = linhas
                .GroupBy(r => r.MemberId)
                .Select(g => g.First())
                .Take(MaxOrganograma)
                .ToList();
            var totalDoSetor = nos.Count;

            // Completa a cadeia: gestores acima que ficaram de fora (outro setor ou corte do limite)
            var presentes = nos.Select(n => n.MemberId).ToHashSet();
            for (var nivel = 0; nivel < Profundidade; nivel++)
            {
                var faltantes = nos
                    .Where(n => n.GestorMemberId is Guid g && !presentes.Contains(g))
                    .Select(n => n.GestorMemberId!.Value)
                    .Distinct()
                    .ToList();
                if (faltantes.Count == 0) break;

                var gestores = await Resumos().Where(r => faltantes.Contains(r.MemberId)).ToListAsync(ct);
                var novos = gestores.GroupBy(g => g.MemberId).Select(g => g.First()).ToList();
                foreach (var gestor in novos)
                {
                    gestor.ForaDoSetor = setor is not null && !string.Equals(gestor.Departamento, setor, StringComparison.Ordinal);
                    presentes.Add(gestor.MemberId);
                    nos.Add(gestor);
                }

                // Gestores inexistentes (não deveria ocorrer com a FK): evita repetir a busca
                foreach (var id in faltantes) presentes.Add(id);
            }

            return new OrganogramaDto
            {
                Departamento = setor,
                Nos = nos,
                TotalDoSetor = totalDoSetor,
                Truncado = truncado,
            };
        }
    }
}

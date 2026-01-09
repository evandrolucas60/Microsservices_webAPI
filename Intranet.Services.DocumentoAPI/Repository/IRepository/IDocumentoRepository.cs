using Intranet.Services.DocumentoAPI.Models;
using Intranet.Services.DocumentoAPI.Pagination;

namespace Intranet.Services.DocumentoAPI.Repository.IRepository;

public interface IDocumentoRepository : IRepository<Documento>
{
    Task<PagedList<Documento>> GetDocumentosAsync(DocumentoParameters escalasParameters);
    Task<PagedList<Documento>> GetDocumentosFiltroNomeAsync(DocumentoFilterNome documentoFilterNome);
    Task<PagedList<Documento>> GetDocumentosFiltroDepartamentoAsync(DocumentoFilterDepartamento escalasParameters);
}

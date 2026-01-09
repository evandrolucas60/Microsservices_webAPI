using Intranet.Services.DocumentoAPI.Data;
using Intranet.Services.DocumentoAPI.Models;
using Intranet.Services.DocumentoAPI.Pagination;
using Intranet.Services.DocumentoAPI.Repository.IRepository;

namespace Intranet.Services.EscalaAPI.Repository;

public class DocumentoRepository : Repository<Documento>, IDocumentoRepository
{
    public DocumentoRepository(AppDbContext context) : base(context) { }

    public async Task<PagedList<Documento>> GetDocumentosAsync(DocumentoParameters documentoParameters)
    {
        var documentos = await GetAllAsync();
        var documentosOrdenados = documentos.OrderBy(x => x.Nome).AsQueryable();
        var resultado = PagedList<Documento>.toPagedList(documentosOrdenados, documentoParameters.PageNumber, documentoParameters.PageSize);
        return resultado;
    }

    public async Task<PagedList<Documento>> GetDocumentosFiltroDepartamentoAsync(DocumentoFilterDepartamento documentoFilterDepartamento)
    {
        var documentos = await GetAllAsync();

        if (!string.IsNullOrEmpty(documentoFilterDepartamento.Departamento))
        {
            string departamentoFiltro = documentoFilterDepartamento.Departamento;
            documentos = documentos.Where(c => c.Departamento.IndexOf(departamentoFiltro, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        var colaboradoresFiltrados = PagedList<Documento>.toPagedList(
            documentos.AsQueryable().OrderBy(c => c.Departamento),
            documentoFilterDepartamento.PageNumber,
            documentoFilterDepartamento.PageSize);

        return colaboradoresFiltrados;

    }

    public async Task<PagedList<Documento>> GetDocumentosFiltroNomeAsync(DocumentoFilterNome documentoFilterNome)
    {
        var documentos = await GetAllAsync();

        if (!string.IsNullOrEmpty(documentoFilterNome.Nome))
        {
            string nomeFiltro = documentoFilterNome.Nome;
            documentos = documentos.Where(c => c.Nome.IndexOf(nomeFiltro, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        var documentosFiltrados = PagedList<Documento>.toPagedList(
            documentos.AsQueryable().OrderBy(c => c.Nome),
            documentoFilterNome.PageNumber,
            documentoFilterNome.PageSize);

        return documentosFiltrados;
    }
}

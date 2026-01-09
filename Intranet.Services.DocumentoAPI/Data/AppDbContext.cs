using Intranet.Services.DocumentoAPI.Models;
using Microsoft.EntityFrameworkCore;

namespace Intranet.Services.DocumentoAPI.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Documento> documentos { get; set; }

    protected override void OnModelCreating(ModelBuilder mb)
    {
        base.OnModelCreating(mb);
    }
}

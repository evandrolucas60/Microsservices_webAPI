using Intranet.Services.EscalaAPI.Models;
using Microsoft.EntityFrameworkCore;

namespace Intranet.Services.EscalaAPI.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Escala> escalas { get; set; }

    protected override void OnModelCreating(ModelBuilder mb)
    {
        base.OnModelCreating(mb);
    }
}

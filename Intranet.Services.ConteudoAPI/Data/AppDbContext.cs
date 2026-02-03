using Intranet.Services.ConteudoAPI.Models;
using Microsoft.EntityFrameworkCore;

namespace Intranet.Services.ConteudoAPI.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Comment> comments { get; set; }
    public DbSet<Post> posts { get; set; }
    public DbSet<Like> likes { get; set; }
}


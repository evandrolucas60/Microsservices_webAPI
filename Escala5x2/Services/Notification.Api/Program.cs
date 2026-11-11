using Escala5x2.BuildingBlocks.Shared.Auth;
using Escala5x2.BuildingBlocks.Shared.Messaging;
using Escala5x2.BuildingBlocks.Shared.Persistence;
using Escala5x2.BuildingBlocks.Shared.Web;
using Microsoft.EntityFrameworkCore;
using Notification.Api.Consumers;
using Notification.Api.Domain;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.Services.AddPostgres<NotificationDbContext>(builder.Configuration);
builder.Services.AddEventBus(builder.Configuration, x => x.AddConsumer<NotificationConsumers>());

var app = builder.Build();
app.UseServiceDefaults();

// Avisos da Escala 5x2 (tela "Avisos"): só o superAdmin da intranet (Admin) visualiza e marca como lido.
// Os eventos continuam sendo gravados para todas as lojas; o sino da intranet (NotificacaoAPI) é outro serviço.
var group = app.MapGroup("/api/notifications").RequireAuthorization(Policies.Admin);
// Arquivos de log do serviço: /api/notifications/logs
app.MapLogFileEndpoints("/api/notifications");
group.MapGet("/", async (Guid? storeId, bool? unreadOnly, NotificationDbContext db, ICurrentUser user) =>
{
    // Hoje só o Admin chega aqui (política acima); a regra por loja vale se a tela for aberta a outros papéis:
    // loja informada precisa ser permitida (perfil ∪ roles de loja); sem loja, os gerais + os das lojas permitidas
    if (storeId is { } informada && !user.CanAccessStore(informada)) return Results.Forbid();
    var q = db.Notifications.AsNoTracking().AsQueryable();
    if (storeId is { } s) q = q.Where(n => n.StoreId == null || n.StoreId == s);
    else if (!user.HasGlobalScope)
    {
        var permitidas = user.StoreIds.ToList();
        q = q.Where(n => n.StoreId == null || permitidas.Contains(n.StoreId.Value));
    }
    if (unreadOnly == true) q = q.Where(n => n.ReadAt == null);
    return Results.Ok(await q.OrderByDescending(n => n.CreatedAt).Take(100).ToListAsync());
});
group.MapPost("/{id:guid}/read", async (Guid id, NotificationDbContext db) =>
{
    var n = await db.Notifications.FindAsync(id);
    if (n is null) return Results.NotFound();
    n.ReadAt ??= DateTime.UtcNow;
    await db.SaveChangesAsync();
    return Results.NoContent();
});

// Atualiza o banco como os serviços da intranet (ApplyMigrations): aplica as migrations pendentes
await app.Services.ApplyMigrationsAsync<NotificationDbContext>();
app.Run();

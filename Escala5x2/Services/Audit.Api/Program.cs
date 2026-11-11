using Audit.Api.Consumers;
using Audit.Api.Domain;
using Audit.Api.Endpoints;
using Escala5x2.BuildingBlocks.Shared.Auth;
using Escala5x2.BuildingBlocks.Shared.Messaging;
using Escala5x2.BuildingBlocks.Shared.Persistence;
using Escala5x2.BuildingBlocks.Shared.Web;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.Services.AddPostgres<AuditDbContext>(builder.Configuration);
builder.Services.AddEventBus(builder.Configuration, x => x.AddConsumer<AuditConsumer>());

var app = builder.Build();
app.UseServiceDefaults();

app.MapGet("/api/audit", async (string? type, Guid? scheduleId, DateTime? from, DateTime? to, int? page, AuditDbContext db) =>
{
    var q = db.Entries.AsNoTracking().AsQueryable();
    if (!string.IsNullOrWhiteSpace(type)) q = q.Where(e => e.MessageType == type);
    if (scheduleId is { } sid) q = q.Where(e => e.ScheduleId == sid);
    if (from is { } f) q = q.Where(e => e.OccurredAt >= DateTime.SpecifyKind(f, DateTimeKind.Utc));
    if (to is { } t) q = q.Where(e => e.OccurredAt <= DateTime.SpecifyKind(t, DateTimeKind.Utc));
    var size = 50;
    var p = Math.Max(1, page ?? 1);
    var items = await q.OrderByDescending(e => e.OccurredAt).Skip((p - 1) * size).Take(size).ToListAsync();
    return Results.Ok(new { page = p, size, items });
    // Só o superAdmin da intranet (Admin) visualiza a auditoria
}).RequireAuthorization(Policies.Admin);
// Exportar CSV (mesmos filtros) e arquivos de log do serviço
app.MapAuditExportEndpoints();

// Atualiza o banco como os serviços da intranet (ApplyMigrations): aplica as migrations pendentes
await app.Services.ApplyMigrationsAsync<AuditDbContext>();
app.Run();

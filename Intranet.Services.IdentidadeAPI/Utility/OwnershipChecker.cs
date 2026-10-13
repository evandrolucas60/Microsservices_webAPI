using System.Security.Claims;
using Intranet.Services.IdentidadeAPI.Data;
using Microsoft.EntityFrameworkCore;

namespace Intranet.Services.IdentidadeAPI.Utility;

/// <summary>
/// Verifica se o usuário do token é dono do registro (ou Admin).
/// Cadeia de propriedade: Member.UserId = claim "id";
/// Work/Address/Profile → MemberId; Education → ProfileId → MemberId.
/// </summary>
public interface IOwnershipChecker
{
    string? UserId(ClaimsPrincipal user);
    bool IsAdmin(ClaimsPrincipal user);
    bool IsUser(ClaimsPrincipal user, string? userId);
    Task<bool> OwnsMemberAsync(ClaimsPrincipal user, Guid memberId);
    Task<bool> OwnsProfileAsync(ClaimsPrincipal user, Guid profileId);
    Task<bool> OwnsWorkAsync(ClaimsPrincipal user, int workId);
    Task<bool> OwnsEducationAsync(ClaimsPrincipal user, int educationId);
    Task<bool> OwnsAddressAsync(ClaimsPrincipal user, int addressId);
}

public class OwnershipChecker : IOwnershipChecker
{
    public const string AdminRole = "Admin";

    private readonly AppDbContext _db;

    public OwnershipChecker(AppDbContext db)
    {
        _db = db;
    }

    public string? UserId(ClaimsPrincipal user) => user.FindFirst("id")?.Value;

    public bool IsAdmin(ClaimsPrincipal user) => user.IsInRole(AdminRole);

    public bool IsUser(ClaimsPrincipal user, string? userId) =>
        IsAdmin(user) ||
        (!string.IsNullOrEmpty(userId) && string.Equals(UserId(user), userId, StringComparison.OrdinalIgnoreCase));

    public async Task<bool> OwnsMemberAsync(ClaimsPrincipal user, Guid memberId)
    {
        if (IsAdmin(user)) return true;
        var userId = UserId(user);
        if (string.IsNullOrEmpty(userId)) return false;

        return await _db.Members.AsNoTracking()
            .AnyAsync(m => m.MemberId == memberId && m.UserId == userId);
    }

    public async Task<bool> OwnsProfileAsync(ClaimsPrincipal user, Guid profileId)
    {
        if (IsAdmin(user)) return true;
        var memberId = await _db.Profiles.AsNoTracking()
            .Where(p => p.ProfileId == profileId)
            .Select(p => (Guid?)p.MemberId)
            .FirstOrDefaultAsync();
        return memberId.HasValue && await OwnsMemberAsync(user, memberId.Value);
    }

    public async Task<bool> OwnsWorkAsync(ClaimsPrincipal user, int workId)
    {
        if (IsAdmin(user)) return true;
        var memberId = await _db.Works.AsNoTracking()
            .Where(w => w.WorkId == workId)
            .Select(w => (Guid?)w.MemberId)
            .FirstOrDefaultAsync();
        return memberId.HasValue && await OwnsMemberAsync(user, memberId.Value);
    }

    public async Task<bool> OwnsEducationAsync(ClaimsPrincipal user, int educationId)
    {
        if (IsAdmin(user)) return true;
        var profileId = await _db.Educations.AsNoTracking()
            .Where(e => e.EducationId == educationId)
            .Select(e => (Guid?)e.ProfileId)
            .FirstOrDefaultAsync();
        return profileId.HasValue && await OwnsProfileAsync(user, profileId.Value);
    }

    public async Task<bool> OwnsAddressAsync(ClaimsPrincipal user, int addressId)
    {
        if (IsAdmin(user)) return true;
        var memberId = await _db.Addresses.AsNoTracking()
            .Where(a => a.AddressId == addressId)
            .Select(a => (Guid?)a.MemberId)
            .FirstOrDefaultAsync();
        return memberId.HasValue && await OwnsMemberAsync(user, memberId.Value);
    }
}

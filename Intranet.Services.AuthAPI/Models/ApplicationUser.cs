using Microsoft.AspNetCore.Identity;

namespace Intranet.Services.AuthAPI.Models;

public class ApplicationUser : IdentityUser
{
    public string? RefreshToken { get; set; }
    public DateTime RefreshTokenExpiryTime { get; set; }
}

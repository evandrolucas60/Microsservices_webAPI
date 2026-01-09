using System.ComponentModel.DataAnnotations;

namespace Intranet.Services.AuthAPI.Models.DTOs;

public class LoginModel
{
    [Required(ErrorMessage = "Username is required!!")]
    public string? Username { get; set; }
    [Required(ErrorMessage = "Password is required!!")]
    public string? Password { get; set; }
}

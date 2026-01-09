namespace Intranet.Services.AuthAPI.Models.DTOs
{
    public class TokenModel
    {
        public string? AccessToken { get; set; }
        public string? RefreshToken { get; set; }
    }
}

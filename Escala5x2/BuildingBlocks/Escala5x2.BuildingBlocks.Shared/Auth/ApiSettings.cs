namespace Escala5x2.BuildingBlocks.Shared.Auth;

/// <summary>
/// JWT da intranet (emitido pelo Intranet.Services.AuthAPI no login). Mesma seção "ApiSettings" e mesmos
/// valores dos serviços da intranet: chave simétrica em ASCII, issuer e audience validados.
/// </summary>
public sealed class ApiSettings
{
    public const string Section = "ApiSettings";
    public string Secret { get; set; } = string.Empty;
    public string Issuer { get; set; } = string.Empty;
    public string Audience { get; set; } = string.Empty;
}

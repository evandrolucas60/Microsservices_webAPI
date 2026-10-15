using Microsoft.AspNetCore.Authentication;
using System.Net.Http.Headers;

namespace Intranet.Services.IdentidadeAPI.Utility;

public class BackendApiAuthenticationHttpClientHandler : DelegatingHandler
{
    private readonly IHttpContextAccessor _accessor;
    public BackendApiAuthenticationHttpClientHandler(IHttpContextAccessor accessor)
    {
        _accessor = accessor;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Visitante (sem login): segue sem token para os endpoints públicos
        var context = _accessor.HttpContext;
        var token = context is null ? null : await context.GetTokenAsync("access_token");
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await base.SendAsync(request, cancellationToken);
    }
}

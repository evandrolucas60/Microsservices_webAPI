using System.Net.WebSockets;

namespace Intranet.Services.ComunicacaoAPI.Services;

/// <summary>
/// Endpoint WebSocket do chat (/ws/chat?access_token=...). Autenticado pelo JWT
/// (lido da query string, pois o navegador não envia cabeçalhos no WebSocket).
/// Eventos enviados ao cliente: presence-snapshot, presence, message, read.
/// </summary>
public static class ChatSocketEndpoint
{
    public const string Path = "/ws/chat";

    public static IEndpointConventionBuilder MapChatSocket(this IEndpointRouteBuilder app, string[] allowedOrigins)
    {
        return app.Map(Path, async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            // CORS não se aplica a WebSocket: valida a origem para evitar uso por outros sites
            var origin = context.Request.Headers.Origin.ToString();
            if (!string.IsNullOrEmpty(origin) && !allowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            var userId = context.User.FindFirst("id")?.Value;
            var token = context.Request.Query["access_token"].ToString();
            var identidade = context.RequestServices.GetRequiredService<IdentidadeClient>();
            var memberId = userId is null ? null : await identidade.GetMemberIdAsync(userId, token, context.RequestAborted);
            if (memberId is null)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            var manager = context.RequestServices.GetRequiredService<ChatConnectionManager>();
            var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("ChatSocket");

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            var (connectionId, cameOnline) = manager.Add(memberId.Value, socket);
            try
            {
                await manager.SendToConnectionAsync(memberId.Value, connectionId,
                    new { type = "presence-snapshot", online = manager.OnlineMembers() });
                if (cameOnline)
                {
                    await manager.BroadcastAsync(new { type = "presence", memberId = memberId.Value, online = true });
                }

                // O cliente só envia "ping"; o conteúdo é descartado (mensagens vão pela API REST)
                var buffer = new byte[1024];
                while (socket.State == WebSocketState.Open && !context.RequestAborted.IsCancellationRequested)
                {
                    var result = await socket.ReceiveAsync(buffer, context.RequestAborted);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                        break;
                    }
                }
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
            {
                logger.LogDebug(ex, "Conexão do chat encerrada");
            }
            finally
            {
                if (manager.Remove(memberId.Value, connectionId))
                {
                    await manager.BroadcastAsync(new { type = "presence", memberId = memberId.Value, online = false });
                }
            }
        }).RequireAuthorization();
    }
}

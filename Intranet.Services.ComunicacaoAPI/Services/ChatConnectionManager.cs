using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Intranet.Services.ComunicacaoAPI.Services;

/// <summary>
/// Conexões WebSocket do chat por colaborador (um colaborador pode ter várias abas abertas).
/// Define a presença (online = ao menos uma conexão) e entrega os eventos em tempo real.
/// Observação: o estado fica na memória desta instância; com várias réplicas do serviço
/// seria necessário um backplane (ex.: Redis pub/sub).
/// </summary>
public sealed class ChatConnectionManager
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed class Connection
    {
        public required WebSocket Socket { get; init; }
        // WebSocket não aceita envios simultâneos
        public SemaphoreSlim SendLock { get; } = new(1, 1);
    }

    private readonly object _sync = new();
    private readonly Dictionary<Guid, Dictionary<Guid, Connection>> _byMember = new();
    private readonly ILogger<ChatConnectionManager> _logger;

    public ChatConnectionManager(ILogger<ChatConnectionManager> logger)
    {
        _logger = logger;
    }

    /// <summary>Registra a conexão. CameOnline indica que é a primeira conexão do colaborador.</summary>
    public (Guid ConnectionId, bool CameOnline) Add(Guid memberId, WebSocket socket)
    {
        var connectionId = Guid.NewGuid();
        lock (_sync)
        {
            if (!_byMember.TryGetValue(memberId, out var connections))
            {
                connections = new Dictionary<Guid, Connection>();
                _byMember[memberId] = connections;
            }
            connections[connectionId] = new Connection { Socket = socket };
            return (connectionId, connections.Count == 1);
        }
    }

    /// <summary>Remove a conexão. Retorna true quando o colaborador ficou offline.</summary>
    public bool Remove(Guid memberId, Guid connectionId)
    {
        lock (_sync)
        {
            if (!_byMember.TryGetValue(memberId, out var connections)) return false;
            connections.Remove(connectionId);
            if (connections.Count > 0) return false;
            _byMember.Remove(memberId);
            return true;
        }
    }

    public bool IsOnline(Guid memberId)
    {
        lock (_sync) return _byMember.ContainsKey(memberId);
    }

    public IReadOnlyList<Guid> OnlineMembers()
    {
        lock (_sync) return _byMember.Keys.ToList();
    }

    /// <summary>Envia o evento a todas as conexões dos colaboradores informados.</summary>
    public Task SendToMembersAsync(IEnumerable<Guid> memberIds, object payload)
    {
        List<Connection> targets;
        lock (_sync)
        {
            targets = memberIds.Distinct()
                .Where(_byMember.ContainsKey)
                .SelectMany(id => _byMember[id].Values)
                .ToList();
        }
        return SendAsync(targets, payload);
    }

    /// <summary>Envia o evento a uma conexão específica.</summary>
    public Task SendToConnectionAsync(Guid memberId, Guid connectionId, object payload)
    {
        Connection? target = null;
        lock (_sync)
        {
            if (_byMember.TryGetValue(memberId, out var connections)) connections.TryGetValue(connectionId, out target);
        }
        return target is null ? Task.CompletedTask : SendAsync(new[] { target }, payload);
    }

    /// <summary>Envia o evento a todos os colaboradores conectados (presença).</summary>
    public Task BroadcastAsync(object payload)
    {
        List<Connection> targets;
        lock (_sync) targets = _byMember.Values.SelectMany(c => c.Values).ToList();
        return SendAsync(targets, payload);
    }

    private async Task SendAsync(IEnumerable<Connection> targets, object payload)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, JsonOptions));
        await Task.WhenAll(targets.Select(async connection =>
        {
            if (connection.Socket.State != WebSocketState.Open) return;
            await connection.SendLock.WaitAsync();
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await connection.Socket.SendAsync(bytes, WebSocketMessageType.Text, true, timeout.Token);
            }
            catch (Exception ex)
            {
                // Conexão caída: o loop de leitura dela fará a limpeza
                _logger.LogDebug(ex, "Falha ao enviar evento do chat");
            }
            finally
            {
                connection.SendLock.Release();
            }
        }));
    }
}

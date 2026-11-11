using System.Collections.Concurrent;

namespace Optimization.Worker.Engine;

/// <summary>
/// Gerações em andamento nesta réplica do Worker (por escala) e cancelamentos recebidos. O cancelamento chega a todas as
/// réplicas (fila temporária por instância): a que está calculando interrompe o CP-SAT pelo token da geração; as outras
/// guardam o pedido e descartam o comando se ele ainda estiver na fila (ou chegar depois, por retentativa).
/// Cancelamentos antigos são esquecidos depois de <see cref="Retention"/>.
/// </summary>
public sealed class GenerationRegistry(TimeProvider? clock = null)
{
    /// <summary>Por quanto tempo um cancelamento recebido é lembrado (comando ainda na fila ou reentregue).</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromHours(6);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ConcurrentDictionary<Guid, Running> _running = new();
    /// <summary>Cancelamentos por (escala, geração); geração vazia (Guid.Empty) = qualquer geração da escala.</summary>
    private readonly ConcurrentDictionary<(Guid ScheduleId, Guid GenerationId), DateTimeOffset> _cancelled = new();

    internal sealed record Running(Guid? GenerationId, CancellationTokenSource Source);

    /// <summary>Gerações calculando agora nesta réplica.</summary>
    public int RunningCount => _running.Count;

    /// <summary>A geração já foi cancelada (antes de começar ou durante)?</summary>
    public bool IsCancelled(Guid scheduleId, Guid? generationId)
    {
        Forget();
        return _cancelled.ContainsKey((scheduleId, Guid.Empty))
               || (generationId is { } g && _cancelled.ContainsKey((scheduleId, g)));
    }

    /// <summary>
    /// Registra a geração que vai calcular e devolve o token dela (ligado ao token do consumidor). Quem chama descarta o
    /// registro com <see cref="Finish"/> (o using do retorno faz isso).
    /// </summary>
    public Lease Start(Guid scheduleId, Guid? generationId, CancellationToken consumer)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(consumer);
        var running = new Running(generationId, source);
        _running.AddOrUpdate(scheduleId, running, (_, old) =>
        {
            // Outra geração da mesma escala ainda calculando nesta réplica (pedido repetido): a nova vale, a antiga para
            TryCancel(old.Source);
            return running;
        });
        // Cancelamento que chegou entre a checagem e o registro
        if (IsCancelled(scheduleId, generationId)) TryCancel(source);
        return new Lease(this, scheduleId, running);
    }

    /// <summary>Recebe um cancelamento: guarda e interrompe a geração da escala se estiver calculando aqui.</summary>
    /// <returns>true quando havia geração calculando nesta réplica.</returns>
    public bool Cancel(Guid scheduleId, Guid? generationId)
    {
        Forget();
        _cancelled[(scheduleId, generationId ?? Guid.Empty)] = _clock.GetUtcNow();
        if (!_running.TryGetValue(scheduleId, out var running)) return false;
        if (generationId is { } g && running.GenerationId is { } current && current != g) return false; // outra geração
        TryCancel(running.Source);
        return true;
    }

    private void Finish(Guid scheduleId, Running running)
    {
        _running.TryRemove(new KeyValuePair<Guid, Running>(scheduleId, running));
        running.Source.Dispose();
    }

    private static void TryCancel(CancellationTokenSource source)
    {
        try
        {
            source.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // geração já terminou
        }
    }

    private void Forget()
    {
        var limit = _clock.GetUtcNow() - Retention;
        foreach (var (key, at) in _cancelled)
            if (at < limit) _cancelled.TryRemove(key, out _);
    }

    /// <summary>Geração registrada: o token dela e o descarte do registro.</summary>
    public sealed class Lease : IDisposable
    {
        private readonly GenerationRegistry _owner;
        private readonly Guid _scheduleId;
        private readonly Running _running;
        private int _disposed;

        internal Lease(GenerationRegistry owner, Guid scheduleId, Running running)
        {
            _owner = owner;
            _scheduleId = scheduleId;
            _running = running;
        }

        public CancellationToken Token => _running.Source.Token;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) _owner.Finish(_scheduleId, _running);
        }
    }
}

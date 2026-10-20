using System.Collections.Concurrent;
using System.Threading.Channels;
using Basalt.Application.Ports.Events;
using Basalt.Domain.Vaults;
using Microsoft.Extensions.Logging;

namespace Basalt.Application.Events;

/// <summary>
/// Channel-based <see cref="IEventBus"/> (spec 01 §4.3): one ordered queue per vault (plus one for application-wide
/// events), so a slow subscriber of one vault never delays another. Subscriber failures are logged and isolated.
/// </summary>
public sealed partial class InMemoryEventBus(ILogger<InMemoryEventBus> logger) : IEventBus, IAsyncDisposable
{
    private readonly ConcurrentDictionary<VaultId, Queue> _queues = new();
    private readonly Queue _global = new();
    private readonly Lock _subscriptionsGate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Lock _idleGate = new();
    private ImmutableSubscriptions _subscriptions = ImmutableSubscriptions.Empty;
    private int _pending;
    private TaskCompletionSource _idle = CompletedIdle();
    private bool _disposed;

    public ValueTask PublishAsync<T>(T evt, CancellationToken ct)
        where T : IApplicationEvent
    {
        ArgumentNullException.ThrowIfNull(evt);
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_idleGate)
        {
            if (_pending++ == 0)
            {
                _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        var queue = evt.VaultId is { } vault ? _queues.GetOrAdd(vault, _ => new Queue()) : _global;
        queue.Ensure(this);
        queue.Channel.Writer.TryWrite(evt);
        return ValueTask.CompletedTask;
    }

    public IDisposable Subscribe<T>(Func<T, CancellationToken, ValueTask> handler)
        where T : IApplicationEvent
    {
        ArgumentNullException.ThrowIfNull(handler);
        var subscription = new Subscription(typeof(T), (evt, ct) => handler((T)evt, ct));
        lock (_subscriptionsGate)
        {
            _subscriptions = _subscriptions.Add(subscription);
        }

        return new Unsubscriber(this, subscription);
    }

    /// <summary>Completes when every published event has been delivered (tests and orderly shutdown).</summary>
    public Task WaitForIdleAsync(CancellationToken ct)
    {
        lock (_idleGate)
        {
            return _idle.Task.WaitAsync(ct);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var queue in _queues.Values.Append(_global))
        {
            queue.Channel.Writer.TryComplete();
        }

        await _shutdown.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(_queues.Values.Append(_global).Select(q => q.Worker ?? Task.CompletedTask)).ConfigureAwait(false);
        _shutdown.Dispose();
    }

    private async Task RunAsync(Queue queue)
    {
        try
        {
            await foreach (var evt in queue.Channel.Reader.ReadAllAsync(_shutdown.Token).ConfigureAwait(false))
            {
                foreach (var subscription in _subscriptions.Items)
                {
                    if (!subscription.Type.IsInstanceOfType(evt))
                    {
                        continue;
                    }

                    try
                    {
                        await subscription.Handler(evt, _shutdown.Token).ConfigureAwait(false);
                    }
#pragma warning disable CA1031 // A failing subscriber must not break delivery to the others (spec 01 §4.3).
                    catch (Exception ex) when (ex is not OperationCanceledException || !_shutdown.IsCancellationRequested)
#pragma warning restore CA1031
                    {
                        LogSubscriberFailed(logger, evt.GetType().Name, ex);
                    }
                }

                lock (_idleGate)
                {
                    if (--_pending == 0)
                    {
                        _idle.TrySetResult();
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
    }

    private static TaskCompletionSource CompletedIdle()
    {
        var tcs = new TaskCompletionSource();
        tcs.SetResult();
        return tcs;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Subscriber failed while handling {EventType}")]
    private static partial void LogSubscriberFailed(ILogger logger, string eventType, Exception exception);

    private sealed class Queue
    {
        private readonly Lock _gate = new();

        public Channel<object> Channel { get; } = System.Threading.Channels.Channel.CreateUnbounded<object>(new UnboundedChannelOptions { SingleReader = true });

        public Task? Worker { get; private set; }

        public void Ensure(InMemoryEventBus bus)
        {
            if (Worker is not null)
            {
                return;
            }

            lock (_gate)
            {
                Worker ??= Task.Run(() => bus.RunAsync(this));
            }
        }
    }

    private sealed record Subscription(Type Type, Func<object, CancellationToken, ValueTask> Handler);

    private sealed record ImmutableSubscriptions(IReadOnlyList<Subscription> Items)
    {
        public static ImmutableSubscriptions Empty { get; } = new([]);

        public ImmutableSubscriptions Add(Subscription subscription) => new([.. Items, subscription]);

        public ImmutableSubscriptions Remove(Subscription subscription) => new([.. Items.Where(s => !ReferenceEquals(s, subscription))]);
    }

    private sealed class Unsubscriber(InMemoryEventBus bus, Subscription subscription) : IDisposable
    {
        public void Dispose()
        {
            lock (bus._subscriptionsGate)
            {
                bus._subscriptions = bus._subscriptions.Remove(subscription);
            }
        }
    }
}

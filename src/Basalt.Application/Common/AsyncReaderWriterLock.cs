namespace Basalt.Application.Common;

/// <summary>
/// Asynchronous reader/writer lock (spec 01 §4.4): concurrent readers, exclusive writers, FIFO with writer preference
/// (a waiting writer blocks new readers). Leases release on dispose.
/// </summary>
public sealed class AsyncReaderWriterLock : IDisposable
{
    private readonly Lock _gate = new();
    private readonly LinkedList<Waiter> _queue = new();
    private int _readers;
    private bool _writer;

    public Task<IDisposable> ReadAsync(CancellationToken ct) => AcquireAsync(isWriter: false, ct);

    public Task<IDisposable> WriteAsync(CancellationToken ct) => AcquireAsync(isWriter: true, ct);

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var waiter in _queue)
            {
                waiter.Completion.TrySetCanceled();
            }

            _queue.Clear();
        }
    }

    private Task<IDisposable> AcquireAsync(bool isWriter, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_queue.Count == 0 && !_writer && (!isWriter || _readers == 0))
            {
                Grant(isWriter);
                return Task.FromResult<IDisposable>(new Lease(this, isWriter));
            }

            var waiter = new Waiter(isWriter, new TaskCompletionSource<IDisposable>(TaskCreationOptions.RunContinuationsAsynchronously));
            var node = _queue.AddLast(waiter);
            if (ct.CanBeCanceled)
            {
                waiter.Registration = ct.Register(() => Cancel(node, ct));
            }

            return waiter.Completion.Task;
        }
    }

    private void Cancel(LinkedListNode<Waiter> node, CancellationToken ct)
    {
        lock (_gate)
        {
            if (node.List is null)
            {
                return;
            }

            _queue.Remove(node);
            node.Value.Completion.TrySetCanceled(ct);
            Pump();
        }
    }

    private void Release(bool isWriter)
    {
        lock (_gate)
        {
            if (isWriter)
            {
                _writer = false;
            }
            else
            {
                _readers--;
            }

            Pump();
        }
    }

    // Grants the head of the queue: one writer, or every consecutive reader.
    private void Pump()
    {
        while (_queue.First is { } head && !_writer)
        {
            var waiter = head.Value;
            if (waiter.IsWriter && _readers > 0)
            {
                return;
            }

            _queue.RemoveFirst();
            waiter.Registration.Dispose();
            Grant(waiter.IsWriter);
            if (!waiter.Completion.TrySetResult(new Lease(this, waiter.IsWriter)))
            {
                Ungrant(waiter.IsWriter);
                continue;
            }

            if (waiter.IsWriter)
            {
                return;
            }
        }
    }

    private void Grant(bool isWriter)
    {
        if (isWriter)
        {
            _writer = true;
        }
        else
        {
            _readers++;
        }
    }

    private void Ungrant(bool isWriter)
    {
        if (isWriter)
        {
            _writer = false;
        }
        else
        {
            _readers--;
        }
    }

    private sealed class Waiter(bool isWriter, TaskCompletionSource<IDisposable> completion)
    {
        public bool IsWriter { get; } = isWriter;

        public TaskCompletionSource<IDisposable> Completion { get; } = completion;

        public CancellationTokenRegistration Registration { get; set; }
    }

    private sealed class Lease(AsyncReaderWriterLock owner, bool isWriter) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                owner.Release(isWriter);
            }
        }
    }
}

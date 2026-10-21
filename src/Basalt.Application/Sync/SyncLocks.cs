using System.Collections.Concurrent;
using Basalt.Domain.Vaults;

namespace Basalt.Application.Sync;

/// <summary>
/// One sync lease per vault (spec 06 §6, step 0): content editing is not blocked, only a second sync of the same vault
/// is refused.
/// </summary>
public sealed class SyncLocks : IDisposable
{
    private readonly ConcurrentDictionary<VaultId, SemaphoreSlim> _locks = new();

    public async Task<IDisposable?> TryAcquireAsync(VaultId vault, CancellationToken ct)
    {
        var gate = _locks.GetOrAdd(vault, _ => new SemaphoreSlim(1, 1));
        return await gate.WaitAsync(0, ct).ConfigureAwait(false) ? new Lease(gate) : null;
    }

    public void Dispose()
    {
        foreach (var gate in _locks.Values)
        {
            gate.Dispose();
        }
    }

    private sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        private bool _released;

        public void Dispose()
        {
            if (!_released)
            {
                _released = true;
                gate.Release();
            }
        }
    }
}

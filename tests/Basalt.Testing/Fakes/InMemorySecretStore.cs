using System.Collections.Concurrent;
using Basalt.Application.Ports.Security;
using Basalt.Domain.Common;

namespace Basalt.Testing.Fakes;

/// <summary>In-memory <see cref="ISecretStore"/> for tests (spec 03 §1.5).</summary>
public sealed class InMemorySecretStore : ISecretStore
{
    private readonly ConcurrentDictionary<string, byte[]> _secrets = new(StringComparer.Ordinal);

    public Task<Result<Unit>> SetAsync(string key, ReadOnlyMemory<byte> secret, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _secrets[key] = secret.ToArray();
        return Task.FromResult(Result<Unit>.Success(Unit.Value));
    }

    public Task<Result<byte[]?>> GetAsync(string key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Result<byte[]?>.Success(
            _secrets.TryGetValue(key, out var secret) ? secret.ToArray() : null));
    }

    public Task<Result<Unit>> DeleteAsync(string key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _secrets.TryRemove(key, out _);
        return Task.FromResult(Result<Unit>.Success(Unit.Value));
    }
}

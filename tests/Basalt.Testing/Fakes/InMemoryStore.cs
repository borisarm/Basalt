using Basalt.Application.Ports.Storage;
using Basalt.Domain.Common;

namespace Basalt.Testing.Fakes;

/// <summary>In-memory <see cref="IDocumentStore{T}"/>; <see cref="Corrupt"/> simulates an unreadable document.</summary>
public sealed class InMemoryStore<T>(Func<T> defaultValue) : IDocumentStore<T>
    where T : class
{
    private readonly Lock _gate = new();
    private T? _value;
    private bool _corrupt;

    public int SaveCount { get; private set; }

    public Error? FailNextSave { get; set; }

    public void Corrupt()
    {
        lock (_gate)
        {
            _corrupt = true;
        }
    }

    public Task<DocumentLoad<T>> LoadAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            if (_corrupt)
            {
                return Task.FromResult(new DocumentLoad<T>(defaultValue(), DocumentLoadStatus.Invalid, "corrupt"));
            }

            return Task.FromResult(_value is null
                ? new DocumentLoad<T>(defaultValue(), DocumentLoadStatus.Missing)
                : new DocumentLoad<T>(_value, DocumentLoadStatus.Loaded));
        }
    }

    public Task<Result<Unit>> SaveAsync(T document, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(document);
        lock (_gate)
        {
            if (FailNextSave is { } error)
            {
                FailNextSave = null;
                return Task.FromResult(Result.Failure(error));
            }

            _value = document;
            _corrupt = false;
            SaveCount++;
            return Task.FromResult(Result.Success());
        }
    }
}

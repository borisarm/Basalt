using Basalt.Domain.Common;

namespace Basalt.Application.Ports.Storage;

public enum DocumentLoadStatus
{
    Loaded,

    /// <summary>The document does not exist yet; the default value is returned.</summary>
    Missing,

    /// <summary>The document is unreadable (invalid JSON, newer schema...); the default value is returned and the file
    /// is kept until the user changes something (it is backed up before being replaced).</summary>
    Invalid,
}

/// <summary>Result of loading a document: the value (or its default) and how it was obtained.</summary>
public sealed record DocumentLoad<T>(T Value, DocumentLoadStatus Status, string? Problem = null);

/// <summary>
/// A single persisted document with a schema version (spec 08 §1.1). Reads are tolerant; writes are atomic. Output
/// port: fake <c>InMemoryStore&lt;T&gt;</c>, real <c>JsonFileStore&lt;T&gt;</c>, contract <c>KeyValueStoreContract</c>.
/// </summary>
public interface IDocumentStore<T>
    where T : class
{
    Task<DocumentLoad<T>> LoadAsync(CancellationToken ct);

    /// <returns><c>storage.io</c> when the document cannot be written.</returns>
    Task<Result<Unit>> SaveAsync(T document, CancellationToken ct);
}

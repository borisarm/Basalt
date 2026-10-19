using Basalt.Application.Ports.FileSystem;
using Basalt.Domain.Notes;

namespace Basalt.Adapters.FileSystem;

/// <summary>
/// Merges raw watcher notifications within a debounce window into the minimal set of changes per path. Basalt's atomic
/// writes (temporary file renamed onto the target) become a single <see cref="FileSystemChangeKind.Changed"/>.
/// Not thread-safe: callers serialise access.
/// </summary>
internal sealed class ChangeCoalescer
{
    private readonly Dictionary<VaultPath, (FileSystemChangeKind Kind, VaultPath? OldPath)> _pending = [];
    private readonly List<VaultPath> _order = [];
    private bool _rescan;

    public bool HasPending => _rescan || _pending.Count > 0;

    public void Overflow() => _rescan = true;

    public void Add(FileSystemChangeKind kind, string? relativePath, string? oldRelativePath = null)
    {
        var path = ToVaultPath(relativePath);
        var oldPath = ToVaultPath(oldRelativePath);
        var isTemp = IsTemp(relativePath);
        var oldIsTemp = IsTemp(oldRelativePath);

        if (kind == FileSystemChangeKind.Renamed)
        {
            if (isTemp || path is null)
            {
                // Renamed into a temporary/unaddressable name: from the vault's point of view it disappeared.
                if (oldPath is { } gone && !oldIsTemp)
                {
                    Record(gone, FileSystemChangeKind.Deleted, null);
                }

                return;
            }

            if (oldIsTemp || oldPath is null)
            {
                // Atomic write: the temp file replaced (or created) the target.
                Record(path.Value, FileSystemChangeKind.Changed, null);
                return;
            }

            Remove(oldPath.Value);
            Record(path.Value, FileSystemChangeKind.Renamed, oldPath);
            return;
        }

        if (isTemp || path is null)
        {
            return;
        }

        Record(path.Value, kind, null);
    }

    /// <summary>Returns the coalesced changes and resets the state.</summary>
    public IReadOnlyList<FileSystemChange> Flush()
    {
        var result = new List<FileSystemChange>();
        if (_rescan)
        {
            result.Add(new FileSystemChange(FileSystemChangeKind.RescanRequired, VaultPath.Root));
        }
        else
        {
            foreach (var path in _order)
            {
                if (_pending.TryGetValue(path, out var change))
                {
                    result.Add(new FileSystemChange(change.Kind, path, change.OldPath));
                }
            }
        }

        _pending.Clear();
        _order.Clear();
        _rescan = false;
        return result;
    }

    private void Record(VaultPath path, FileSystemChangeKind kind, VaultPath? oldPath)
    {
        if (!_pending.TryGetValue(path, out var previous))
        {
            _pending[path] = (kind, oldPath);
            _order.Add(path);
            return;
        }

        var merged = (previous.Kind, kind) switch
        {
            (FileSystemChangeKind.Created, FileSystemChangeKind.Deleted) => ((FileSystemChangeKind Kind, VaultPath? OldPath)?)null,
            (FileSystemChangeKind.Created, _) => (FileSystemChangeKind.Created, null),
            (FileSystemChangeKind.Deleted, FileSystemChangeKind.Created) => (FileSystemChangeKind.Changed, null),
            (FileSystemChangeKind.Renamed, FileSystemChangeKind.Changed) => previous,
            _ => (kind, oldPath),
        };

        if (merged is null)
        {
            Remove(path);
        }
        else
        {
            _pending[path] = merged.Value;
        }
    }

    private void Remove(VaultPath path)
    {
        _pending.Remove(path);
        _order.Remove(path);
    }

    private static bool IsTemp(string? relativePath) =>
        relativePath is not null && VaultFileConventions.IsTemporaryFile(Path.GetFileName(relativePath));

    private static VaultPath? ToVaultPath(string? relativePath)
    {
        if (relativePath is null)
        {
            return null;
        }

        var result = VaultPath.Create(relativePath);
        return result.IsSuccess && !result.Value.IsRoot ? result.Value : null;
    }
}

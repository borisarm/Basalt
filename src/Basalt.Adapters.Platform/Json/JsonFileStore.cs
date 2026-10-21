using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Basalt.Application.Errors;
using Basalt.Application.Ports.Storage;
using Basalt.Domain.Common;

namespace Basalt.Adapters.Platform.Json;

/// <summary>Upgrades a document from <see cref="FromVersion"/> to <c>FromVersion + 1</c>.</summary>
internal sealed record JsonMigration(int FromVersion, Func<JsonObject, JsonObject> Migrate);

/// <summary>
/// One JSON document in <c>{AppData}</c> (spec 08 §1.1): <c>schemaVersion</c> with explicit migrations, atomic writes
/// (temp + rename) and tolerant reads — an unreadable or newer document yields the default value and is backed up
/// (<c>*.corrupt-{timestamp}</c>) before the first save replaces it.
/// </summary>
internal sealed class JsonFileStore<T>(
    string path,
    JsonTypeInfo<T> typeInfo,
    Func<T> defaultValue,
    int currentVersion,
    IReadOnlyList<JsonMigration> migrations,
    bool indented,
    TimeProvider time) : IDocumentStore<T>, IDisposable
    where T : class
{
    private const string VersionProperty = "schemaVersion";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _needsBackup;

    public string Path { get; } = path;

    public async Task<DocumentLoad<T>> LoadAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!File.Exists(Path))
            {
                return new DocumentLoad<T>(defaultValue(), DocumentLoadStatus.Missing);
            }

            var text = await File.ReadAllTextAsync(Path, ct).ConfigureAwait(false);
            var (value, problem) = Parse(text);
            _needsBackup = value is null;
            return value is null
                ? new DocumentLoad<T>(defaultValue(), DocumentLoadStatus.Invalid, problem)
                : new DocumentLoad<T>(value, DocumentLoadStatus.Loaded);
        }
        catch (IOException ex)
        {
            _needsBackup = true;
            return new DocumentLoad<T>(defaultValue(), DocumentLoadStatus.Invalid, ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            _needsBackup = true;
            return new DocumentLoad<T>(defaultValue(), DocumentLoadStatus.Invalid, ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<Result<Unit>> SaveAsync(T document, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(document);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            if (_needsBackup && File.Exists(Path))
            {
                // Never lose data: keep the unreadable version next to the new one.
                var stamp = time.GetUtcNow().ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
                File.Copy(Path, $"{Path}.corrupt-{stamp}", overwrite: false);
            }

            var node = (JsonObject)JsonSerializer.SerializeToNode(document, typeInfo)!;
            var versioned = new JsonObject { [VersionProperty] = currentVersion };
            foreach (var (key, value) in node.ToList())
            {
                node.Remove(key);
                versioned[key] = value;
            }

            var json = versioned.ToJsonString(new JsonSerializerOptions { WriteIndented = indented, NewLine = "\n" });
            var temp = $"{Path}.~basalt-{Guid.NewGuid():N}.tmp";
            await File.WriteAllTextAsync(temp, indented ? json + "\n" : json, new UTF8Encoding(false), ct).ConfigureAwait(false);
            File.Move(temp, Path, overwrite: true);
            _needsBackup = false;
            return Result.Success();
        }
        catch (IOException ex)
        {
            return new Error(ErrorCodes.StorageIo, ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            return new Error(ErrorCodes.StorageIo, ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<Result<Unit>> DeleteAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (File.Exists(Path))
            {
                File.Delete(Path);
            }

            _needsBackup = false;
            return Result.Success();
        }
        catch (IOException ex)
        {
            return new Error(ErrorCodes.StorageIo, ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            return new Error(ErrorCodes.StorageIo, ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private (T? Value, string? Problem) Parse(string text)
    {
        try
        {
            if (JsonNode.Parse(text) is not JsonObject root)
            {
                return (null, "The document is not a JSON object.");
            }

            var version = root[VersionProperty]?.GetValue<int>() ?? 1;
            if (version > currentVersion)
            {
                return (null, $"Schema version {version} is newer than the supported {currentVersion}.");
            }

            for (; version < currentVersion; version++)
            {
                var migration = migrations.FirstOrDefault(m => m.FromVersion == version);
                if (migration is null)
                {
                    return (null, $"No migration from schema version {version}.");
                }

                root = migration.Migrate(root);
            }

            root.Remove(VersionProperty);
            return (root.Deserialize(typeInfo), null);
        }
        catch (JsonException ex)
        {
            return (null, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return (null, ex.Message);
        }
        catch (FormatException ex)
        {
            return (null, ex.Message);
        }
    }
}

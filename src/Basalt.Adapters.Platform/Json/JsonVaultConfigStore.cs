using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Basalt.Application.Bookmarks;
using Basalt.Application.Errors;
using Basalt.Application.Ports.FileSystem;
using Basalt.Application.Ports.Storage;
using Basalt.Application.Workspace;
using Basalt.Domain.Common;
using Basalt.Domain.Notes;

namespace Basalt.Adapters.Platform.Json;

/// <summary>
/// <c>.basalt/vault.json</c> through the vault file system (spec 08 §1): indented JSON so that GitHub diffs are
/// readable, <c>schemaVersion</c>, tolerant reads (e.g. after a sync conflict left markers in the file).
/// </summary>
internal sealed class JsonVaultConfigStore : IVaultConfigStore
{
    public const int SchemaVersion = 1;
    private static readonly VaultPath ConfigPath = VaultPath.Create(".basalt/vault.json").Value;
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, NewLine = "\n" };

    public async Task<DocumentLoad<VaultConfig?>> LoadVaultConfigAsync(IVaultFileSystem vault, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(vault);
        var text = await vault.ReadAllTextAsync(ConfigPath, ct).ConfigureAwait(false);
        if (text.IsFailure)
        {
            return text.Error.Code == ErrorCodes.FsNotFound
                ? new DocumentLoad<VaultConfig?>(null, DocumentLoadStatus.Missing)
                : new DocumentLoad<VaultConfig?>(null, DocumentLoadStatus.Invalid, text.Error.Message);
        }

        try
        {
            if (JsonNode.Parse(text.Value) is not JsonObject root)
            {
                return Invalid("The document is not a JSON object.");
            }

            if ((root["schemaVersion"]?.GetValue<int>() ?? 1) > SchemaVersion)
            {
                return Invalid("The vault was written by a newer version of Basalt.");
            }

            root.Remove("schemaVersion");

            // Missing settings keep their defaults: overlay what the file has on the default settings.
            var settings = (JsonObject)JsonSerializer.SerializeToNode(new VaultConfigDto(default, Domain.Vaults.VaultSettings.Default), PlatformJsonContext.Default.VaultConfigDto)!["settings"]!.DeepClone();
            if (root["settings"] is JsonObject stored)
            {
                foreach (var (key, value) in stored.ToList())
                {
                    stored.Remove(key);
                    settings[key] = value;
                }
            }

            root["settings"] = settings;
            var dto = root.Deserialize(PlatformJsonContext.Default.VaultConfigDto);
            return dto is null
                ? Invalid("Empty document.")
                : new DocumentLoad<VaultConfig?>(new VaultConfig(dto.VaultId, dto.Settings), DocumentLoadStatus.Loaded);
        }
        catch (JsonException ex)
        {
            return Invalid(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return Invalid(ex.Message);
        }
        catch (FormatException ex)
        {
            return Invalid(ex.Message);
        }
    }

    public async Task<Result<Unit>> SaveVaultConfigAsync(IVaultFileSystem vault, VaultConfig config, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(vault);
        ArgumentNullException.ThrowIfNull(config);
        var node = (JsonObject)JsonSerializer.SerializeToNode(new VaultConfigDto(config.Id, config.Settings), PlatformJsonContext.Default.VaultConfigDto)!;
        var document = new JsonObject { ["schemaVersion"] = SchemaVersion };
        foreach (var (key, value) in node.ToList())
        {
            node.Remove(key);
            document[key] = value;
        }

        var bytes = Encoding.UTF8.GetBytes(document.ToJsonString(Indented) + "\n");
        return await vault.WriteAllBytesAsync(ConfigPath, bytes, null, ct).ConfigureAwait(false);
    }

    public async Task<DocumentLoad<BookmarksDocument>> LoadBookmarksAsync(IVaultFileSystem vault, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(vault);
        var (root, status, problem) = await VaultJson.ReadAsync(vault, BookmarksJson.Path, BookmarksJson.SchemaVersion, ct).ConfigureAwait(false);
        if (root is null)
        {
            return new DocumentLoad<BookmarksDocument>(BookmarksDocument.Empty, status, problem);
        }

        var (document, readProblem) = BookmarksJson.Read(root);
        return document is null
            ? new DocumentLoad<BookmarksDocument>(BookmarksDocument.Empty, DocumentLoadStatus.Invalid, readProblem)
            : new DocumentLoad<BookmarksDocument>(document, DocumentLoadStatus.Loaded);
    }

    public async Task<Result<Unit>> SaveBookmarksAsync(IVaultFileSystem vault, BookmarksDocument bookmarks, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(vault);
        ArgumentNullException.ThrowIfNull(bookmarks);
        if ((await LoadBookmarksAsync(vault, ct).ConfigureAwait(false)).Status == DocumentLoadStatus.Invalid)
        {
            var kept = await VaultJson.KeepAsideAsync(vault, BookmarksJson.Path, ct).ConfigureAwait(false);
            if (kept.IsFailure)
            {
                return kept;
            }
        }

        return await VaultJson.WriteAsync(vault, BookmarksJson.Path, BookmarksJson.SchemaVersion, BookmarksJson.Write(bookmarks), ct).ConfigureAwait(false);
    }

    public async Task<DocumentLoad<WorkspaceState?>> LoadWorkspaceAsync(IVaultFileSystem vault, Guid deviceId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(vault);
        var (root, status, problem) = await VaultJson.ReadAsync(vault, WorkspaceJson.PathFor(deviceId), WorkspaceJson.SchemaVersion, ct).ConfigureAwait(false);
        if (root is null)
        {
            return new DocumentLoad<WorkspaceState?>(null, status, problem);
        }

        try
        {
            return root.Deserialize(PlatformJsonContext.Default.WorkspaceState) is { } workspace
                ? new DocumentLoad<WorkspaceState?>(workspace, DocumentLoadStatus.Loaded)
                : Invalid<WorkspaceState>("Empty document.");
        }
        catch (JsonException ex)
        {
            return Invalid<WorkspaceState>(ex.Message);
        }
    }

    public Task<Result<Unit>> SaveWorkspaceAsync(IVaultFileSystem vault, Guid deviceId, WorkspaceState workspace, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(vault);
        ArgumentNullException.ThrowIfNull(workspace);
        var content = (JsonObject)JsonSerializer.SerializeToNode(workspace, PlatformJsonContext.Default.WorkspaceState)!;
        return VaultJson.WriteAsync(vault, WorkspaceJson.PathFor(deviceId), WorkspaceJson.SchemaVersion, content, ct);
    }

    private static DocumentLoad<T?> Invalid<T>(string problem)
        where T : class => new(null, DocumentLoadStatus.Invalid, problem);

    private static DocumentLoad<VaultConfig?> Invalid(string problem) => new(null, DocumentLoadStatus.Invalid, problem);
}

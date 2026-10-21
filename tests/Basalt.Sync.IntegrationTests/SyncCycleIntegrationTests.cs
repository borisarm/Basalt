using System.Text;
using Basalt.Application.Common;
using Basalt.Application.Ports.Events;
using Basalt.Application.Ports.FileSystem;
using Basalt.Application.Ports.Platform;
using Basalt.Application.Ports.Remote;
using Basalt.Application.Ports.Storage;
using Basalt.Application.Sync;
using Basalt.Composition;
using Basalt.Domain.Accounts;
using Basalt.Domain.Notes;
using Basalt.Domain.Sync;
using Basalt.Domain.Vaults;
using Basalt.Testing;
using Basalt.Testing.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Basalt.Sync.IntegrationTests;

/// <summary>End-to-end cycles over a real vault folder, real JSON sync state and an in-memory remote (spec 09 §7).</summary>
public sealed class SyncCycleIntegrationTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly RemoteRepositoryRef Ref = new(GitHubHost.GitHubDotCom, "acme", "vault", "main");

    private readonly TempDirectory _appData = TempDirectory.Create("sync-it");
    private readonly TempDirectory _vaultRoot = TempDirectory.Create("sync-vault");
    private readonly InMemoryRemoteRepository _remote = new(Ref);
    private ServiceProvider _provider = null!;
    private VaultSyncEngine _engine = null!;
    private IVaultFileSystem _files = null!;
    private SyncProfile _profile = null!;

    public async ValueTask InitializeAsync()
    {
        var services = new ServiceCollection()
            .AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning))
            .AddBasaltCore(_appData.Path);
        _provider = services.BuildServiceProvider();

        var location = Path.Combine(_vaultRoot.Path, "vault");
        _files = (await _provider.GetRequiredService<IVaultFileSystemFactory>().CreateAsync(location, Ct)).Value;
        var vault = new VaultId(Guid.NewGuid());
        var configs = _provider.GetRequiredService<IVaultConfigStore>();
        (await configs.SaveVaultConfigAsync(_files, new VaultConfig(vault, VaultSettings.Default), Ct))
            .IsSuccess.Should().BeTrue();
        _profile = new SyncProfile(
            vault,
            new GitHubAccountId(Guid.NewGuid()),
            Ref,
            true,
            false,
            new SyncTriggers(),
            SyncPolicy.Default,
            SyncScope.Default,
            new CommitSettings());
        _engine = new VaultSyncEngine(
            vault,
            _files,
            _remote,
            _provider.GetRequiredService<ISyncStateStoreFactory>().Create(vault),
            _provider.GetRequiredService<IEventBus>(),
            _provider.GetRequiredService<IClock>(),
            _provider.GetRequiredService<IDeviceInfo>(),
            TimeProvider.System,
            new SyncLocks(),
            new VaultLocks());
    }

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _appData.DisposeAsync();
        await _vaultRoot.DisposeAsync();
    }

    [Fact]
    [Trait("Spec", "SYNC-UPLOAD")]
    public async Task LocalNote_IsPublishedAndASecondCycleIsUpToDate()
    {
        await WriteAsync("Notes/A.md", "# A");
        var first = await CycleAsync();
        var second = await CycleAsync();

        first.Status.Should().Be(SyncCycleStatus.Published);
        second.Status.Should().Be(SyncCycleStatus.UpToDate);
        var tree = await _remote.GetTreeAsync((await _remote.GetHeadAsync(null, Ct)).Value.Head!.Tree, Ct);
        tree.Value.Files.Should().Contain(file => file.Path.ToString() == "Notes/A.md");
    }

    [Fact]
    [Trait("Spec", "SYNC-DOWNLOAD")]
    public async Task RemoteNote_IsWrittenToDisk()
    {
        var config = (await _files.ReadAllBytesAsync(VaultPath.Create(".basalt/vault.json").Value, Ct)).Value;
        await CommitRemoteAsync(("Notes/B.md", "# B"));
        var head = (await _remote.GetHeadAsync(null, Ct)).Value.Head!;
        var configBlob = (await _remote.CreateBlobAsync(config, Ct)).Value;
        var tree = (await _remote.CreateTreeAsync(
            head.Tree, [new TreeChange.Upsert(VaultPath.Create(".basalt/vault.json").Value, configBlob)], Ct)).Value;
        var commit = (await _remote.CreateCommitAsync(
            "config", tree, [head.Commit], new CommitAuthor("Other", "other@example.com"), Ct)).Value;
        (await _remote.UpdateBranchAsync(commit, head.Commit, Ct)).IsSuccess.Should().BeTrue();

        var result = await CycleAsync();

        result.Status.Should().Be(SyncCycleStatus.Downloaded);
        (await _files.ReadAllTextAsync(VaultPath.Create("Notes/B.md").Value, Ct)).Value.Should().Be("# B");
    }

    [Fact]
    [Trait("Spec", "SYNC-CONFLICT")]
    public async Task DivergentText_CreatesAConflictCopy()
    {
        await WriteAsync("Notes/C.md", "local");
        await CycleAsync();
        await WriteAsync("Notes/C.md", "local edit");
        await CommitRemoteAsync(("Notes/C.md", "remote"));

        var result = await CycleAsync();

        result.Status.Should().Be(SyncCycleStatus.Merged);
        var files = new List<string>();
        await foreach (var entry in _files.EnumerateAsync(VaultPath.Root, true, Ct))
        {
            files.Add(entry.Path.ToString());
        }

        files.Should().Contain(path => path.Contains(" (conflict ", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Spec", "SYNC-CRLF-BOM")]
    public async Task CrlfAndBom_ArePreservedOnUpload()
    {
        byte[] content = [0xEF, 0xBB, 0xBF, (byte)'a', (byte)'\r', (byte)'\n'];
        await _files.WriteAllBytesAsync(VaultPath.Create("Notes/CRLF.md").Value, content, null, Ct);
        await CycleAsync();
        var head = (await _remote.GetHeadAsync(null, Ct)).Value.Head!;
        var tree = await _remote.GetTreeAsync(head.Tree, Ct);
        var file = tree.Value.Files.Single(entry => entry.Path.ToString() == "Notes/CRLF.md");
        (await _remote.GetBlobAsync(file.Sha, Ct)).Value.Should().Equal(content);
    }

    [Fact]
    [Trait("Spec", "SYNC-IGNORE-WORKSPACE")]
    public async Task WorkspaceState_IsNeverPublished()
    {
        await WriteAsync(".basalt/workspace.dev.json", "{\"open\":[]}");
        await WriteAsync("Notes/W.md", "# W");
        await CycleAsync();
        var head = (await _remote.GetHeadAsync(null, Ct)).Value.Head!;
        var tree = await _remote.GetTreeAsync(head.Tree, Ct);
        tree.Value.Files.Should().NotContain(file => file.Path.ToString().StartsWith(".basalt/workspace.", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Spec", "SYNC-BINARY")]
    public async Task BinaryFile_RoundTripsWithoutTextProcessing()
    {
        var content = new byte[] { 0, 1, 2, 255, 0, 42 };
        await _files.WriteAllBytesAsync(VaultPath.Create("Assets/image.bin").Value, content, null, Ct);
        var result = await CycleAsync();
        result.Status.Should().Be(SyncCycleStatus.Published);
        var head = (await _remote.GetHeadAsync(null, Ct)).Value.Head!;
        var tree = await _remote.GetTreeAsync(head.Tree, Ct);
        var file = tree.Value.Files.Single(entry => entry.Path.ToString() == "Assets/image.bin");
        (await _remote.GetBlobAsync(file.Sha, Ct)).Value.Should().Equal(content);
    }

    [Fact]
    [Trait("Spec", "SYNC-MERGE")]
    public async Task NonOverlappingTextEdits_MergeWithoutAConflictCopy()
    {
        await WriteAsync("Notes/M.md", "one\n");
        await CycleAsync();
        await WriteAsync("Notes/M.md", "one\ntwo\n");
        await CommitRemoteAsync(("Notes/M.md", "zero\none\n"));

        var result = await CycleAsync();

        result.Status.Should().BeOneOf(SyncCycleStatus.Merged, SyncCycleStatus.Published);
        var text = (await _files.ReadAllTextAsync(VaultPath.Create("Notes/M.md").Value, Ct)).Value;
        text.Should().Contain("zero").And.Contain("two").And.NotContain("<<<<<<<");
    }

    [Fact]
    [Trait("Spec", "SYNC-DELETE-REMOTE")]
    public async Task RemoteDeletion_IsAppliedLocally()
    {
        await WriteAsync("Notes/D.md", "# D");
        var first = await CycleAsync();
        first.Status.Should().Be(SyncCycleStatus.Published);
        await CommitChangesAsync(new TreeChange.Delete(VaultPath.Create("Notes/D.md").Value));
        var remoteTree = await _remote.GetTreeAsync((await _remote.GetHeadAsync(null, Ct)).Value.Head!.Tree, Ct);
        remoteTree.Value.Files.Should().NotContain(file => file.Path == VaultPath.Create("Notes/D.md").Value);

        var result = await CycleAsync();

        result.Status.Should().Be(SyncCycleStatus.Downloaded);
        (await _files.StatAsync(VaultPath.Create("Notes/D.md").Value, Ct)).IsFailure.Should().BeTrue();
    }

    [Fact]
    [Trait("Spec", "SYNC-DELETE-LOCAL")]
    public async Task LocalDeletion_IsPublished()
    {
        var path = VaultPath.Create("Notes/E.md").Value;
        await WriteAsync("Notes/E.md", "# E");
        await CycleAsync();
        (await _files.DeleteAsync(path, Ct)).IsSuccess.Should().BeTrue();

        var result = await CycleAsync();

        result.Status.Should().Be(SyncCycleStatus.Published);
        var head = (await _remote.GetHeadAsync(null, Ct)).Value.Head!;
        var tree = await _remote.GetTreeAsync(head.Tree, Ct);
        tree.Value.Files.Should().NotContain(file => file.Path == path);
    }
    private Task<SyncCycleResult> CycleAsync() =>
        _engine.RunAsync(
            new SyncCycleRequest(_profile, true, true, new CommitAuthor("Test", "test@example.com"), "Vault"),
            Ct);

    private async Task WriteAsync(string path, string content)
    {
        (await _files.WriteAllBytesAsync(VaultPath.Create(path).Value, Encoding.UTF8.GetBytes(content), null, Ct))
            .IsSuccess.Should().BeTrue();
    }

    private async Task CommitChangesAsync(params TreeChange[] changes)
    {
        var head = (await _remote.GetHeadAsync(null, Ct)).Value.Head;
        var tree = (await _remote.CreateTreeAsync(head?.Tree, changes, Ct)).Value;
        var commit = (await _remote.CreateCommitAsync(
            "remote change", tree, head is null ? [] : [head.Commit],
            new CommitAuthor("Other", "other@example.com"), Ct)).Value;
        (await _remote.UpdateBranchAsync(commit, head?.Commit, Ct)).IsSuccess.Should().BeTrue();
    }
    private async Task CommitRemoteAsync(params (string Path, string Content)[] files)
    {
        var head = (await _remote.GetHeadAsync(null, Ct)).Value.Head;
        var changes = new List<TreeChange>();
        foreach (var (path, content) in files)
        {
            var blob = (await _remote.CreateBlobAsync(Encoding.UTF8.GetBytes(content), Ct)).Value;
            changes.Add(new TreeChange.Upsert(VaultPath.Create(path).Value, blob));
        }

        var tree = (await _remote.CreateTreeAsync(head?.Tree, changes, Ct)).Value;
        var commit = (await _remote.CreateCommitAsync(
            "remote edit", tree, head is null ? [] : [head.Commit],
            new CommitAuthor("Other", "other@example.com"), Ct)).Value;
        (await _remote.UpdateBranchAsync(commit, head?.Commit, Ct)).IsSuccess.Should().BeTrue();
    }
}

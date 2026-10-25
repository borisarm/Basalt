using System.Text;
using Basalt.Application.Common;
using Basalt.Application.Errors;
using Basalt.Application.Events;
using Basalt.Application.Ports.Remote;
using Basalt.Application.Sync;
using Basalt.Domain.Accounts;
using Basalt.Domain.Common;
using Basalt.Domain.Notes;
using Basalt.Domain.Sync;
using Basalt.Domain.Vaults;
using Basalt.Testing.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Basalt.Application.Tests.Sync;

public sealed class VaultSyncEngineTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static VaultPath P(string path) => VaultPath.Create(path).Value;

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    private static async Task WriteAsync(InMemoryVaultFileSystem fileSystem, string path, string content) =>
        (await fileSystem.WriteAllBytesAsync(P(path), Utf8(content), null, Ct)).IsSuccess.Should().BeTrue();

    private static async Task<RemoteSnapshot> SnapshotAsync(IRemoteRepository repository) =>
        (await new RemoteSnapshotProvider(repository).GetAsync(Ct)).Value;

    private static async Task<CommitSha> CommitRemoteAsync(
        InMemoryRemoteRepository repository,
        string message,
        params (string Path, string Content)[] files)
    {
        var head = (await repository.GetHeadAsync(null, Ct)).Value.Head;
        var changes = new List<TreeChange>();
        foreach (var (path, content) in files)
        {
            var blob = (await repository.CreateBlobAsync(Utf8(content), Ct)).Value;
            changes.Add(new TreeChange.Upsert(P(path), blob));
        }

        var tree = (await repository.CreateTreeAsync(head?.Tree, changes, Ct)).Value;
        var commit = (await repository.CreateCommitAsync(
            message,
            tree,
            head is { } previous ? [previous.Commit] : [],
            new CommitAuthor("Other Device", "other@example.com"),
            Ct)).Value;
        (await repository.UpdateBranchAsync(commit, head?.Commit, Ct)).IsSuccess.Should().BeTrue();
        return commit;
    }

    private sealed class Harness : IAsyncDisposable
    {
        public Harness(Func<VaultId, CancellationToken, Task>? flush = null, SyncPolicy? policy = null, SyncScope? scope = null)
        {
            Profile = new SyncProfile(
                Vault,
                new GitHubAccountId(Guid.NewGuid()),
                new RemoteRepositoryRef(GitHubHost.GitHubDotCom, "acme", "vault", "main"),
                Enabled: true,
                Paused: false,
                new SyncTriggers(),
                policy ?? SyncPolicy.Default,
                scope ?? SyncScope.Default,
                new CommitSettings());
            Engine = new VaultSyncEngine(
                Vault,
                FileSystem,
                Repository,
                Store,
                Events,
                Clock,
                Device,
                TimeProvider.System,
                Locks,
                VaultLocks,
                flush);
        }

        public VaultId Vault { get; } = new(Guid.NewGuid());

        public InMemoryVaultFileSystem FileSystem { get; } = new();

        public InMemoryRemoteRepository Repository { get; } = new(
            new RemoteRepositoryRef(GitHubHost.GitHubDotCom, "acme", "vault", "main"));

        public InMemorySyncStateStore Store { get; } = new();

        public InMemoryEventBus Events { get; } = new(NullLogger<InMemoryEventBus>.Instance);

        public SyncLocks Locks { get; } = new();

        public VaultLocks VaultLocks { get; } = new();

        public FakeClock Clock { get; } = new();

        public FakeDeviceInfo Device { get; } = new("Test Laptop");

        public SyncProfile Profile { get; }

        public VaultSyncEngine Engine { get; }

        public SyncCycleRequest Request(bool online = true, bool token = true) =>
            new(Profile, online, token, new CommitAuthor("Test User", "test@example.com"), "Vault");

        public async ValueTask DisposeAsync()
        {
            Locks.Dispose();
            VaultLocks.Dispose();
            await Events.DisposeAsync();
        }
    }

    [Fact]
    public async Task Run_WithAnEmptyRemote_PublishesTheVault()
    {
        await using var harness = new Harness();
        await WriteAsync(harness.FileSystem, ".basalt/vault.json", "{}");
        await WriteAsync(harness.FileSystem, "A.md", "A");

        var result = await harness.Engine.RunAsync(harness.Request(), Ct);

        result.Status.Should().Be(SyncCycleStatus.Published);
        result.Base.Manifest.Entries.Select(entry => entry.Path.ToString())
            .Should().Equal(".basalt/vault.json", "A.md");
        var remote = await SnapshotAsync(harness.Repository);
        remote.Manifest.Entries.Select(entry => entry.Path.ToString())
            .Should().Equal(".basalt/vault.json", "A.md");
        (await harness.FileSystem.ReadAllTextAsync(P("A.md"), Ct)).Value.Should().Be("A");
    }

    [Fact]
    public async Task Run_AfterASuccessfulCycle_IsUpToDateAndCreatesNoCommit()
    {
        await using var harness = new Harness();
        await WriteAsync(harness.FileSystem, ".basalt/vault.json", "{}");
        await WriteAsync(harness.FileSystem, "A.md", "A");
        await harness.Engine.RunAsync(harness.Request(), Ct);
        var commits = harness.Repository.CommitWrites;

        var result = await harness.Engine.RunAsync(harness.Request(), Ct);

        result.Status.Should().Be(SyncCycleStatus.UpToDate);
        harness.Repository.CommitWrites.Should().Be(commits);
    }

    [Fact]
    public async Task Run_PullsFilesAddedRemotely()
    {
        await using var harness = new Harness();
        await WriteAsync(harness.FileSystem, ".basalt/vault.json", "{}");
        await harness.Engine.RunAsync(harness.Request(), Ct);
        await CommitRemoteAsync(harness.Repository, "remote", ("remote.md", "from GitHub"));

        var result = await harness.Engine.RunAsync(harness.Request(), Ct);

        result.Status.Should().Be(SyncCycleStatus.Downloaded);
        (await harness.FileSystem.ReadAllTextAsync(P("remote.md"), Ct)).Value.Should().Be("from GitHub");
        result.Base.Manifest.Get(P("remote.md")).Should().NotBeNull();
    }

    [Fact]
    public async Task Run_MergesCleanTextChangesOnBothSides()
    {
        await using var harness = new Harness();
        await WriteAsync(harness.FileSystem, ".basalt/vault.json", "{}");
        await WriteAsync(harness.FileSystem, "A.md", "one\ntwo\nthree\n");
        await harness.Engine.RunAsync(harness.Request(), Ct);
        await WriteAsync(harness.FileSystem, "A.md", "LOCAL\ntwo\nthree\n");
        await CommitRemoteAsync(harness.Repository, "remote edit", ("A.md", "one\ntwo\nREMOTE\n"));

        var result = await harness.Engine.RunAsync(harness.Request(), Ct);

        result.Status.Should().Be(SyncCycleStatus.Published);
        (await harness.FileSystem.ReadAllTextAsync(P("A.md"), Ct)).Value.Should().Be("LOCAL\ntwo\nREMOTE\n");
        var remote = await SnapshotAsync(harness.Repository);
        var remoteContent = await harness.Repository.GetBlobAsync(remote.Manifest.Get(P("A.md"))!.Value.Sha, Ct);
        Encoding.UTF8.GetString(remoteContent.Value).Should().Be("LOCAL\ntwo\nREMOTE\n");
        result.Base.Manifest.Get(P("A.md"))!.Value.Sha.Should().Be(GitBlobHasher.Compute(Utf8("LOCAL\ntwo\nREMOTE\n")));
    }

    [Fact]
    public async Task Run_OnATextConflict_KeepsRemoteAtThePathAndCreatesAConflictCopy()
    {
        await using var harness = new Harness();
        await WriteAsync(harness.FileSystem, ".basalt/vault.json", "{}");
        await WriteAsync(harness.FileSystem, "A.md", "base\n");
        await harness.Engine.RunAsync(harness.Request(), Ct);
        await WriteAsync(harness.FileSystem, "A.md", "local\n");
        await CommitRemoteAsync(harness.Repository, "remote edit", ("A.md", "remote\n"));

        var result = await harness.Engine.RunAsync(harness.Request(), Ct);

        result.Status.Should().Be(SyncCycleStatus.Merged);
        result.Conflicts.Should().Contain(P("A.md"));
        (await harness.FileSystem.ReadAllTextAsync(P("A.md"), Ct)).Value.Should().Be("remote\n");
        var copy = result.Base.Manifest.Entries.Single(entry => entry.Path.Name.Contains("conflict", StringComparison.Ordinal));
        (await harness.FileSystem.ReadAllTextAsync(copy.Path, Ct)).Value.Should().Be("local\n");
        (await SnapshotAsync(harness.Repository)).Manifest.Contains(copy.Path).Should().BeTrue();
    }

    [Fact]
    public async Task Run_DoesNotDeleteRemoteFilesThatAreIgnoredLocally()
    {
        await using var harness = new Harness();
        await WriteAsync(harness.FileSystem, ".basalt/vault.json", "{}");
        await WriteAsync(harness.FileSystem, ".basalt/syncignore", "secret.txt\n");
        await WriteAsync(harness.FileSystem, "secret.txt", "local secret");
        await harness.Repository.CreateInitialCommitAsync(P("secret.txt"), Utf8("remote secret"), "init", Ct);

        var result = await harness.Engine.RunAsync(harness.Request(), Ct);

        result.Status.Should().Be(SyncCycleStatus.Published);
        (await harness.FileSystem.ReadAllTextAsync(P("secret.txt"), Ct)).Value.Should().Be("local secret");
        var remote = await SnapshotAsync(harness.Repository);
        remote.Manifest.Get(P("secret.txt")).Should().NotBeNull();
        result.Base.Manifest.Contains(P("secret.txt")).Should().BeFalse();
    }

    [Fact]
    public async Task Run_DoesNotDeleteRemoteFilesThatAreTooLargeLocally()
    {
        await using var harness = new Harness(policy: new SyncPolicy(MaxFileSizeBytes: 5));
        await WriteAsync(harness.FileSystem, ".basalt/vault.json", "{}");
        await WriteAsync(harness.FileSystem, "big.bin", "0123456789");
        await harness.Repository.CreateInitialCommitAsync(P("big.bin"), Utf8("remote big"), "init", Ct);

        await harness.Engine.RunAsync(harness.Request(), Ct);

        var remote = await SnapshotAsync(harness.Repository);
        remote.Manifest.Get(P("big.bin")).Should().NotBeNull();
    }

    [Fact]
    public async Task Run_RespectsPreconditionsWithoutTouchingTheRemote()
    {
        await using var harness = new Harness();
        var repository = NSubstitute.Substitute.For<IRemoteRepository>();
        var engine = new VaultSyncEngine(
            harness.Vault,
            harness.FileSystem,
            repository,
            harness.Store,
            harness.Events,
            harness.Clock,
            harness.Device,
            TimeProvider.System,
            harness.Locks,
            harness.VaultLocks);

        (await engine.RunAsync(harness.Request() with { Profile = harness.Profile with { Enabled = false } }, Ct)).Status
            .Should().Be(SyncCycleStatus.Disabled);
        (await engine.RunAsync(harness.Request() with { Profile = harness.Profile with { Paused = true } }, Ct)).Status
            .Should().Be(SyncCycleStatus.Paused);
        (await engine.RunAsync(harness.Request(online: false), Ct)).Status.Should().Be(SyncCycleStatus.Offline);
        (await engine.RunAsync(harness.Request(token: false), Ct)).Status.Should().Be(SyncCycleStatus.AuthRequired);
        await repository.DidNotReceive().GetHeadAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Run_WhenAnotherCycleHoldsTheLease_IsAlreadyRunning()
    {
        await using var harness = new Harness();
        using var lease = (await harness.Locks.TryAcquireAsync(harness.Vault, Ct))!;

        var result = await harness.Engine.RunAsync(harness.Request(), Ct);

        result.Status.Should().Be(SyncCycleStatus.AlreadyRunning);
    }

    [Fact]
    [Trait("Spec", "NFR-022")]
    public async Task Run_WhenCancelledDuringTheFlush_ReturnsCancelled()
    {
        using var cancellation = new CancellationTokenSource();
        await using var harness = new Harness(flush: (_, _) =>
        {
            cancellation.Cancel();
            return Task.FromException(new OperationCanceledException(cancellation.Token));
        });

        var result = await harness.Engine.RunAsync(harness.Request(), cancellation.Token);

        result.Status.Should().Be(SyncCycleStatus.Cancelled);
        result.Base.Should().Be(SyncBase.Empty);
        result.Conflicts.Should().BeEmpty();
        result.HasDeferred.Should().BeFalse();
        result.Error.Should().BeNull();
    }

    [Fact]
    public async Task Run_WhenTheFlushTimesOut_ContinuesTheCycle()
    {
        var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await using var harness = new Harness(flush: (_, _) => Task.FromException(new OperationCanceledException(cancelled.Token)));

        var result = await harness.Engine.RunAsync(harness.Request(), Ct);

        result.Status.Should().Be(SyncCycleStatus.UpToDate);
    }

    [Fact]
    public async Task Constructor_RejectsNullDependencies()
    {
        await using var harness = new Harness();
        var valid = new VaultSyncEngine(
            harness.Vault, harness.FileSystem, harness.Repository, harness.Store, harness.Events,
            harness.Clock, harness.Device, TimeProvider.System, harness.Locks, harness.VaultLocks);

        FluentActions.Invoking(() => new VaultSyncEngine(harness.Vault, null!, harness.Repository, harness.Store, harness.Events, harness.Clock, harness.Device, TimeProvider.System, harness.Locks, harness.VaultLocks)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => new VaultSyncEngine(harness.Vault, harness.FileSystem, null!, harness.Store, harness.Events, harness.Clock, harness.Device, TimeProvider.System, harness.Locks, harness.VaultLocks)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => new VaultSyncEngine(harness.Vault, harness.FileSystem, harness.Repository, null!, harness.Events, harness.Clock, harness.Device, TimeProvider.System, harness.Locks, harness.VaultLocks)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => new VaultSyncEngine(harness.Vault, harness.FileSystem, harness.Repository, harness.Store, null!, harness.Clock, harness.Device, TimeProvider.System, harness.Locks, harness.VaultLocks)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => new VaultSyncEngine(harness.Vault, harness.FileSystem, harness.Repository, harness.Store, harness.Events, null!, harness.Device, TimeProvider.System, harness.Locks, harness.VaultLocks)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => new VaultSyncEngine(harness.Vault, harness.FileSystem, harness.Repository, harness.Store, harness.Events, harness.Clock, null!, TimeProvider.System, harness.Locks, harness.VaultLocks)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => new VaultSyncEngine(harness.Vault, harness.FileSystem, harness.Repository, harness.Store, harness.Events, harness.Clock, harness.Device, TimeProvider.System, null!, harness.VaultLocks)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => new VaultSyncEngine(harness.Vault, harness.FileSystem, harness.Repository, harness.Store, harness.Events, harness.Clock, harness.Device, TimeProvider.System, harness.Locks, null!)).Should().Throw<ArgumentNullException>();
        valid.Should().NotBeNull();
    }

    [Fact]
    public async Task Run_RejectsNullRequests()
    {
        await using var harness = new Harness();

        await FluentActions.Awaiting(() => harness.Engine.RunAsync(null!, Ct)).Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task Run_WhenPreconditionsFail_ReturnsAnEmptyResult()
    {
        await using var harness = new Harness();
        var requests = new[]
        {
            (harness.Request() with { Profile = harness.Profile with { Enabled = false } }, SyncCycleStatus.Disabled),
            (harness.Request() with { Profile = harness.Profile with { Paused = true } }, SyncCycleStatus.Paused),
            (harness.Request(online: false), SyncCycleStatus.Offline),
            (harness.Request(token: false), SyncCycleStatus.AuthRequired),
        };

        foreach (var (request, status) in requests)
        {
            var result = await harness.Engine.RunAsync(request, Ct);
            result.Status.Should().Be(status);
            result.Base.Should().Be(SyncBase.Empty);
            result.Conflicts.Should().BeEmpty();
            result.HasDeferred.Should().BeFalse();
            result.Error.Should().BeNull();
        }
    }

    [Fact]
    public async Task Run_WhenTheJournalCannotBeSaved_FailsWithoutPublishing()
    {
        await using var harness = new Harness();
        await WriteAsync(harness.FileSystem, "A.md", "A");
        harness.Store.FailNextSave = nameof(InMemorySyncStateStore.SaveJournalAsync);

        var result = await harness.Engine.RunAsync(harness.Request(), Ct);

        result.Status.Should().Be(SyncCycleStatus.Failed);
        result.Error.Should().NotBeNull();
        harness.Repository.CommitWrites.Should().Be(0);
    }

    [Fact]
    public async Task Run_WhenTheStateCannotBeCommitted_Fails()
    {
        await using var harness = new Harness();
        await WriteAsync(harness.FileSystem, "A.md", "A");
        harness.Store.FailNextSave = nameof(InMemorySyncStateStore.SaveBaseAsync);

        var result = await harness.Engine.RunAsync(harness.Request(), Ct);

        result.Status.Should().Be(SyncCycleStatus.Failed);
        result.Error.Should().NotBeNull();
    }

    [Fact]
    public async Task Run_WhenTheSyncIgnoreCannotBeReadForAnotherReason_Fails()
    {
        await using var harness = new Harness();
        await WriteAsync(harness.FileSystem, "A.md", "A");
        harness.FileSystem.FailNextRead(P(".basalt/syncignore"), new Error(ErrorCodes.FsIo, "disk"));

        var result = await harness.Engine.RunAsync(harness.Request(), Ct);

        result.Status.Should().Be(SyncCycleStatus.Failed);
        result.Error!.Code.Should().Be(ErrorCodes.FsIo);
    }
}



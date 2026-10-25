using System.Text;
using Basalt.Application.Common;
using Basalt.Application.Errors;
using Basalt.Application.Events;
using Basalt.Application.Notes;
using Basalt.Application.Ports.Events;
using Basalt.Application.Ports.FileSystem;
using Basalt.Application.Sync;
using Basalt.Domain.Common;
using Basalt.Domain.Notes;
using Basalt.Domain.Sync;
using Basalt.Domain.Vaults;
using Basalt.Testing.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Basalt.Application.Tests.Sync;

public sealed class LocalApplierTests
{
    private static readonly VaultId Vault = new(Guid.NewGuid());

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static VaultPath P(string path) => VaultPath.Create(path).Value;

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    private static BlobSha Sha(string text) => GitBlobHasher.Compute(Utf8(text));

    private static BlobSha Sha(byte[] content) => GitBlobHasher.Compute(content);

    private static Manifest ManifestOf(params (string Path, byte[] Content)[] files) =>
        Manifest.Create(files.Select(file => new ManifestEntry(P(file.Path), Sha(file.Content), file.Content.LongLength))).Value;

    private static async Task WriteAsync(IVaultFileSystem fileSystem, string path, string content) =>
        (await fileSystem.WriteAllBytesAsync(P(path), Utf8(content), null, Ct)).IsSuccess.Should().BeTrue();

    private static async Task<LocalApplyResult> ApplyAsync(
        IVaultFileSystem fileSystem,
        Manifest local,
        Manifest target,
        IReadOnlyDictionary<BlobSha, byte[]> contents,
        IEventBus events)
    {
        using var locks = new VaultLocks();
        var result = await new LocalApplier(fileSystem, locks, events)
            .ApplyAsync(Vault, local, target, contents, Ct);
        result.IsSuccess.Should().BeTrue(result.IsSuccess ? string.Empty : result.Error.Code);
        return result.Value;
    }

    [Fact]
    public async Task Apply_WritesNewAndModifiedFiles()
    {
        var fileSystem = new InMemoryVaultFileSystem();
        await WriteAsync(fileSystem, "a.md", "old");
        var local = ManifestOf(("a.md", Utf8("old")));
        var target = ManifestOf(("a.md", Utf8("new")), ("b.md", Utf8("B")));
        var contents = new Dictionary<BlobSha, byte[]> { [Sha("new")] = Utf8("new"), [Sha("B")] = Utf8("B") };
        await using var events = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);

        var result = await ApplyAsync(fileSystem, local, target, contents, events);

        result.Applied.Should().Equal(P("a.md"), P("b.md"));
        result.Deferred.Should().BeEmpty();
        (await fileSystem.ReadAllTextAsync(P("a.md"), Ct)).Value.Should().Be("new");
        (await fileSystem.ReadAllTextAsync(P("b.md"), Ct)).Value.Should().Be("B");
    }

    [Fact]
    public async Task Apply_DefersACreationWhenThePathAppearedMeanwhile()
    {
        var fileSystem = new InMemoryVaultFileSystem();
        await WriteAsync(fileSystem, "a.md", "external");
        var target = ManifestOf(("a.md", Utf8("remote")));
        var contents = new Dictionary<BlobSha, byte[]> { [Sha("remote")] = Utf8("remote") };
        await using var events = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);

        var result = await ApplyAsync(fileSystem, Manifest.Empty, target, contents, events);

        result.Applied.Should().BeEmpty();
        result.Deferred.Should().Equal(P("a.md"));
        (await fileSystem.ReadAllTextAsync(P("a.md"), Ct)).Value.Should().Be("external");
    }

    [Fact]
    public async Task Apply_DefersAWriteWhenTheLocalFileChanged()
    {
        var fileSystem = new InMemoryVaultFileSystem();
        await WriteAsync(fileSystem, "a.md", "changed");
        var local = ManifestOf(("a.md", Utf8("old")));
        var target = ManifestOf(("a.md", Utf8("remote")));
        var contents = new Dictionary<BlobSha, byte[]> { [Sha("remote")] = Utf8("remote") };
        await using var events = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);

        var result = await ApplyAsync(fileSystem, local, target, contents, events);

        result.Deferred.Should().Equal(P("a.md"));
        (await fileSystem.ReadAllTextAsync(P("a.md"), Ct)).Value.Should().Be("changed");
    }

    [Fact]
    public async Task Apply_DeletesFilesAndPrunesEmptyFolders()
    {
        var fileSystem = new InMemoryVaultFileSystem();
        await WriteAsync(fileSystem, "dir/a.md", "A");
        await WriteAsync(fileSystem, "dir/sub/b.md", "B");
        var local = ManifestOf(("dir/a.md", Utf8("A")), ("dir/sub/b.md", Utf8("B")));
        await using var events = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);

        var result = await ApplyAsync(fileSystem, local, Manifest.Empty, new Dictionary<BlobSha, byte[]>(), events);

        result.Applied.Should().Equal(P("dir/a.md"), P("dir/sub/b.md"));
        (await fileSystem.ExistsAsync(P("dir"), Ct)).Should().BeFalse();
    }

    [Fact]
    public async Task Apply_DefersADeleteWhenTheFileChanged()
    {
        var fileSystem = new InMemoryVaultFileSystem();
        await WriteAsync(fileSystem, "a.md", "changed");
        var local = ManifestOf(("a.md", Utf8("original")));
        await using var events = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);

        var result = await ApplyAsync(fileSystem, local, Manifest.Empty, new Dictionary<BlobSha, byte[]>(), events);

        result.Applied.Should().BeEmpty();
        result.Deferred.Should().Equal(P("a.md"));
        (await fileSystem.ExistsAsync(P("a.md"), Ct)).Should().BeTrue();
    }

    [Fact]
    public async Task Apply_TreatsAnAlreadyMissingFileAsDeleted()
    {
        var fileSystem = new InMemoryVaultFileSystem();
        var local = ManifestOf(("a.md", Utf8("A")));
        await using var events = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);

        var result = await ApplyAsync(fileSystem, local, Manifest.Empty, new Dictionary<BlobSha, byte[]>(), events);

        result.Applied.Should().Equal(P("a.md"));
        result.Deferred.Should().BeEmpty();
    }

    [Fact]
    public async Task Apply_MovesTheLocalFileInsteadOfRequiringTheDownloadedContent()
    {
        var fileSystem = new InMemoryVaultFileSystem();
        await WriteAsync(fileSystem, "old.md", "X");
        var local = ManifestOf(("old.md", Utf8("X")));
        var target = ManifestOf(("new.md", Utf8("X")));
        await using var events = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);

        var result = await ApplyAsync(fileSystem, local, target, new Dictionary<BlobSha, byte[]>(), events);

        result.Applied.Should().Equal(P("new.md"), P("old.md"));
        (await fileSystem.ExistsAsync(P("old.md"), Ct)).Should().BeFalse();
        (await fileSystem.ReadAllTextAsync(P("new.md"), Ct)).Value.Should().Be("X");
    }

    [Fact]
    public async Task Apply_FallsBackToAWriteWhenTheMoveTargetAlreadyExists()
    {
        var fileSystem = new InMemoryVaultFileSystem();
        await WriteAsync(fileSystem, "old.md", "X");
        await WriteAsync(fileSystem, "new.md", "Y");
        var local = ManifestOf(("new.md", Utf8("Y")), ("old.md", Utf8("X")));
        var target = ManifestOf(("new.md", Utf8("X")));
        var contents = new Dictionary<BlobSha, byte[]> { [Sha("X")] = Utf8("X") };
        await using var events = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);

        var result = await ApplyAsync(fileSystem, local, target, contents, events);

        (await fileSystem.ReadAllTextAsync(P("new.md"), Ct)).Value.Should().Be("X");
        (await fileSystem.ExistsAsync(P("old.md"), Ct)).Should().BeFalse();
        result.Deferred.Should().BeEmpty();
    }

    [Fact]
    public async Task Apply_DoesNotMoveAChangedSourceAndDefersItsDelete()
    {
        var fileSystem = new InMemoryVaultFileSystem();
        await WriteAsync(fileSystem, "old.md", "changed");
        var local = ManifestOf(("old.md", Utf8("X")));
        var target = ManifestOf(("new.md", Utf8("X")));
        var contents = new Dictionary<BlobSha, byte[]> { [Sha("X")] = Utf8("X") };
        await using var events = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);

        var result = await ApplyAsync(fileSystem, local, target, contents, events);

        (await fileSystem.ReadAllTextAsync(P("new.md"), Ct)).Value.Should().Be("X");
        (await fileSystem.ReadAllTextAsync(P("old.md"), Ct)).Value.Should().Be("changed");
        result.Deferred.Should().Equal(P("old.md"));
    }

    [Fact]
    public async Task Apply_RenamesCaseOnly()
    {
        var fileSystem = new InMemoryVaultFileSystem();
        await WriteAsync(fileSystem, "Nota.md", "X");
        var local = ManifestOf(("Nota.md", Utf8("X")));
        var target = ManifestOf(("nota.md", Utf8("X")));
        await using var events = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);

        var result = await ApplyAsync(fileSystem, local, target, new Dictionary<BlobSha, byte[]>(), events);

        result.Deferred.Should().BeEmpty();
        var entries = new List<string>();
        await foreach (var entry in fileSystem.EnumerateAsync(VaultPath.Root, true, Ct))
        {
            entries.Add(entry.Path.ToString());
        }

        entries.Should().Equal("nota.md");
    }

    [Fact]
    public async Task Apply_WithoutChangesDoesNothingAndPublishesNoEvent()
    {
        var fileSystem = new InMemoryVaultFileSystem();
        await WriteAsync(fileSystem, "a.md", "A");
        var manifest = ManifestOf(("a.md", Utf8("A")));
        await using var events = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        var published = new List<FilesChangedBySync>();
        using var subscription = events.Subscribe<FilesChangedBySync>((evt, _) =>
        {
            published.Add(evt);
            return ValueTask.CompletedTask;
        });

        var result = await ApplyAsync(fileSystem, manifest, manifest, new Dictionary<BlobSha, byte[]>(), events);
        await events.WaitForIdleAsync(Ct);

        result.Applied.Should().BeEmpty();
        result.Deferred.Should().BeEmpty();
        published.Should().BeEmpty();
    }

    [Fact]
    public async Task Apply_PublishesFilesChangedBySyncOnceWithEveryAppliedPath()
    {
        var fileSystem = new InMemoryVaultFileSystem();
        await WriteAsync(fileSystem, "old.md", "X");
        var local = ManifestOf(("old.md", Utf8("X")));
        var target = ManifestOf(("new.md", Utf8("X")), ("b.md", Utf8("B")));
        var contents = new Dictionary<BlobSha, byte[]> { [Sha("B")] = Utf8("B") };
        await using var events = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        var published = new List<FilesChangedBySync>();
        using var subscription = events.Subscribe<FilesChangedBySync>((evt, _) =>
        {
            published.Add(evt);
            return ValueTask.CompletedTask;
        });

        await ApplyAsync(fileSystem, local, target, contents, events);
        await events.WaitForIdleAsync(Ct);

        var evt = published.Should().ContainSingle().Subject;
        evt.Id.Should().Be(Vault);
        evt.Paths.Should().Equal(P("b.md"), P("new.md"), P("old.md"));
    }

    [Fact]
    public async Task Apply_WhenAWriteHasNoContent_Fails()
    {
        var fileSystem = new InMemoryVaultFileSystem();
        var target = ManifestOf(("a.md", Utf8("A")));
        using var locks = new VaultLocks();
        await using var events = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);

        var result = await new LocalApplier(fileSystem, locks, events)
            .ApplyAsync(Vault, Manifest.Empty, target, new Dictionary<BlobSha, byte[]>(), Ct);

        result.Error.Code.Should().Be(SyncErrors.ContentMissing);
    }

    [Fact]
    public async Task Apply_PropagatesUnexpectedFileSystemErrors()
    {
        var fileSystem = new InMemoryVaultFileSystem();
        fileSystem.FailNextWrite(P("a.md"), new Error(ErrorCodes.FsAccessDenied, "denied"));
        var target = ManifestOf(("a.md", Utf8("A")));
        var contents = new Dictionary<BlobSha, byte[]> { [Sha("A")] = Utf8("A") };
        using var locks = new VaultLocks();
        await using var events = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);

        var result = await new LocalApplier(fileSystem, locks, events)
            .ApplyAsync(Vault, Manifest.Empty, target, contents, Ct);

        result.Error.Code.Should().Be(ErrorCodes.FsAccessDenied);
    }
    [Fact]
    public void Constructor_RejectsNullDependencies()
    {
        using var locks = new VaultLocks();
        var events = NSubstitute.Substitute.For<IEventBus>();

        FluentActions.Invoking(() => new LocalApplier(null!, locks, events)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => new LocalApplier(NSubstitute.Substitute.For<IVaultFileSystem>(), null!, events)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => new LocalApplier(NSubstitute.Substitute.For<IVaultFileSystem>(), locks, null!)).Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task Apply_RejectsNullArguments()
    {
        using var locks = new VaultLocks();
        await using var events = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        var applier = new LocalApplier(new InMemoryVaultFileSystem(), locks, events);

        await FluentActions.Awaiting(() => applier.ApplyAsync(Vault, null!, Manifest.Empty, new Dictionary<BlobSha, byte[]>(), Ct))
            .Should().ThrowAsync<ArgumentNullException>();
        await FluentActions.Awaiting(() => applier.ApplyAsync(Vault, Manifest.Empty, null!, new Dictionary<BlobSha, byte[]>(), Ct))
            .Should().ThrowAsync<ArgumentNullException>();
        await FluentActions.Awaiting(() => applier.ApplyAsync(Vault, Manifest.Empty, Manifest.Empty, null!, Ct))
            .Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task Apply_WhenPruningAFolderFails_ReturnsTheError()
    {
        var failure = new Error(ErrorCodes.FsAccessDenied, "denied");
        var fileSystem = NSubstitute.Substitute.For<IVaultFileSystem>();
        fileSystem.DeleteIfUnchangedAsync(P("Dir/a.md"), Arg.Any<BlobSha>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success()));
        fileSystem.DeleteDirectoryIfEmptyAsync(P("Dir"), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<Unit>.Failure(failure)));
        using var locks = new VaultLocks();
        await using var events = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        var local = ManifestOf(("Dir/a.md", Utf8("A")));

        var result = await new LocalApplier(fileSystem, locks, events)
            .ApplyAsync(Vault, local, Manifest.Empty, new Dictionary<BlobSha, byte[]>(), Ct);

        result.Error.Should().Be(failure);
    }

    [Fact]
    public async Task Apply_WhenEverythingIsDeferred_DoesNotPublish()
    {
        var fileSystem = new InMemoryVaultFileSystem();
        var target = ManifestOf(("a.md", Utf8("A")));
        fileSystem.FailNextWrite(P("a.md"), new Error(ErrorCodes.FsAlreadyExists, "appeared"));
        using var locks = new VaultLocks();
        await using var events = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        var changed = 0;
        using var subscription = events.Subscribe<FilesChangedBySync>((_, _) => { changed++; return ValueTask.CompletedTask; });

        var result = await new LocalApplier(fileSystem, locks, events)
            .ApplyAsync(Vault, Manifest.Empty, target, new Dictionary<BlobSha, byte[]> { [Sha("A")] = Utf8("A") }, Ct);
        await events.WaitForIdleAsync(Ct);

        result.Value.Applied.Should().BeEmpty();
        result.Value.Deferred.Should().Equal(P("a.md"));
        changed.Should().Be(0);
    }
}


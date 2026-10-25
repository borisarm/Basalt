using Basalt.Application.Ports.Remote;
using Basalt.Application.Sync;
using Basalt.Application.Vaults;
using Basalt.Domain.Common;
using Basalt.Domain.Notes;
using Basalt.Domain.Sync;
using Basalt.Domain.Vaults;
using Basalt.Testing.Fakes;
using NSubstitute;

namespace Basalt.Application.Tests.Sync;

public sealed class HistoryUseCaseTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly VaultId Vault = new(Guid.NewGuid());
    private static readonly VaultPath Path = VaultPath.Create("Notes/A.md").Value;
    private static readonly CommitSha Commit = CommitSha.Parse(new string('a', 40)).Value;

    private readonly InMemoryVaultFileSystem _files = new();
    private readonly IRemoteRepository _remote = Substitute.For<IRemoteRepository>();
    private readonly VaultSessions _sessions = new();

    public async ValueTask InitializeAsync()
    {
        var session = new VaultSession(Vault, "Notes", _files, VaultSettings.Default);
        session.Set(_remote);
        _sessions.Add(session);
        await Task.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _sessions.DisposeAsync();

    [Fact]
    public async Task History_ReturnsCommitsFromTheRemote()
    {
        RemoteCommitInfo[] commits = [new(Commit, "edit", "A", "a@example.com", DateTimeOffset.UnixEpoch)];
        _remote.GetFileHistoryAsync(Path, 10, Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<RemoteCommitInfo>>.Success(commits));

        var result = await new GetFileHistoryHandler(_sessions).HandleAsync(new GetFileHistory(Vault, Path, 10), Ct);

        result.Value.Should().ContainSingle().Which.Sha.Should().Be(Commit);
    }

    [Fact]
    public async Task Version_ReturnsTheContentAtThatCommit()
    {
        _remote.GetFileAtCommitAsync(Path, Commit, Arg.Any<CancellationToken>())
            .Returns(Result<byte[]?>.Success("old"u8.ToArray()));

        var result = await new GetFileVersionHandler(_sessions).HandleAsync(new GetFileVersion(Vault, Path, Commit), Ct);

        result.Value.Should().Equal("old"u8.ToArray());
    }

    [Fact]
    public async Task Restore_WritesTheContentAsALocalEdit()
    {
        _remote.GetFileAtCommitAsync(Path, Commit, Arg.Any<CancellationToken>())
            .Returns(Result<byte[]?>.Success("old"u8.ToArray()));

        var result = await new RestoreFileVersionHandler(_sessions)
            .HandleAsync(new RestoreFileVersion(Vault, Path, Commit), Ct);

        result.IsSuccess.Should().BeTrue();
        (await _files.ReadAllBytesAsync(Path, Ct)).Value.Should().Equal("old"u8.ToArray());
    }

    [Fact]
    public async Task History_WithoutRemote_ReturnsNotConfigured()
    {
        var sessions = new VaultSessions();
        var session = new VaultSession(Vault, "Notes", _files, VaultSettings.Default);
        sessions.Add(session);

        var result = await new GetFileHistoryHandler(sessions).HandleAsync(new GetFileHistory(Vault, Path), Ct);

        result.Error.Code.Should().Be(SyncErrors.NotConfigured);
        await sessions.DisposeAsync();
    }
    [Fact]
    public async Task History_ClampsTheMaximumToOne()
    {
        _remote.GetFileHistoryAsync(Path, 1, Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<RemoteCommitInfo>>.Success([]));

        await new GetFileHistoryHandler(_sessions).HandleAsync(new GetFileHistory(Vault, Path, 0), Ct);

        await _remote.Received(1).GetFileHistoryAsync(Path, 1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task VersionAndRestore_WithoutRemote_AreNotConfigured()
    {
        var sessions = new VaultSessions();
        sessions.Add(new VaultSession(Vault, "Notes", _files, VaultSettings.Default));

        var version = await new GetFileVersionHandler(sessions).HandleAsync(new GetFileVersion(Vault, Path, Commit), Ct);
        var restore = await new RestoreFileVersionHandler(sessions).HandleAsync(new RestoreFileVersion(Vault, Path, Commit), Ct);

        version.Error.Code.Should().Be(SyncErrors.NotConfigured);
        restore.Error.Code.Should().Be(SyncErrors.NotConfigured);
        await sessions.DisposeAsync();
    }

    [Fact]
    public async Task Restore_WhenTheFileDidNotExistInThatCommit_IsNotFound()
    {
        _remote.GetFileAtCommitAsync(Path, Commit, Arg.Any<CancellationToken>())
            .Returns(Result<byte[]?>.Success(null));

        var result = await new RestoreFileVersionHandler(_sessions).HandleAsync(new RestoreFileVersion(Vault, Path, Commit), Ct);

        result.Error.Code.Should().Be(RemoteErrors.NotFound);
        result.Error.Message.Should().Be("The file does not exist in that commit.");
    }

    [Fact]
    public async Task Restore_PropagatesReadAndWriteFailures()
    {
        var readFailure = new Error(RemoteErrors.ServerError, "boom");
        _remote.GetFileAtCommitAsync(Path, Commit, Arg.Any<CancellationToken>())
            .Returns(Result<byte[]?>.Failure(readFailure));
        var read = await new RestoreFileVersionHandler(_sessions).HandleAsync(new RestoreFileVersion(Vault, Path, Commit), Ct);

        _remote.GetFileAtCommitAsync(Path, Commit, Arg.Any<CancellationToken>())
            .Returns(Result<byte[]?>.Success("old"u8.ToArray()));
        var writeFailure = new Error(Basalt.Application.Errors.ErrorCodes.FsIo, "disk");
        _files.FailNextWrite(Path, writeFailure);
        var write = await new RestoreFileVersionHandler(_sessions).HandleAsync(new RestoreFileVersion(Vault, Path, Commit), Ct);

        read.Error.Should().Be(readFailure);
        write.Error.Should().Be(writeFailure);
    }

    [Fact]
    public async Task HistoryAndVersion_PropagateRemoteFailures()
    {
        var failure = new Error(RemoteErrors.Network, "offline");
        _remote.GetFileHistoryAsync(Path, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<RemoteCommitInfo>>.Failure(failure));
        _remote.GetFileAtCommitAsync(Path, Commit, Arg.Any<CancellationToken>())
            .Returns(Result<byte[]?>.Failure(failure));

        var history = await new GetFileHistoryHandler(_sessions).HandleAsync(new GetFileHistory(Vault, Path), Ct);
        var version = await new GetFileVersionHandler(_sessions).HandleAsync(new GetFileVersion(Vault, Path, Commit), Ct);

        history.Error.Should().Be(failure);
        version.Error.Should().Be(failure);
    }

    [Fact]
    public async Task HistoryHandlers_RejectNullsAndClosedVaults()
    {
        var closed = new VaultId(Guid.NewGuid());

        await FluentActions.Awaiting(() => new GetFileHistoryHandler(_sessions).HandleAsync(null!, Ct)).Should().ThrowAsync<ArgumentNullException>();
        await FluentActions.Awaiting(() => new GetFileVersionHandler(_sessions).HandleAsync(null!, Ct)).Should().ThrowAsync<ArgumentNullException>();
        await FluentActions.Awaiting(() => new RestoreFileVersionHandler(_sessions).HandleAsync(null!, Ct)).Should().ThrowAsync<ArgumentNullException>();

        (await new GetFileHistoryHandler(_sessions).HandleAsync(new GetFileHistory(closed, Path), Ct)).Error.Code.Should().Be(Basalt.Application.Errors.ErrorCodes.VaultNotOpen);
        (await new GetFileVersionHandler(_sessions).HandleAsync(new GetFileVersion(closed, Path, Commit), Ct)).Error.Code.Should().Be(Basalt.Application.Errors.ErrorCodes.VaultNotOpen);
        (await new RestoreFileVersionHandler(_sessions).HandleAsync(new RestoreFileVersion(closed, Path, Commit), Ct)).Error.Code.Should().Be(Basalt.Application.Errors.ErrorCodes.VaultNotOpen);
    }
}

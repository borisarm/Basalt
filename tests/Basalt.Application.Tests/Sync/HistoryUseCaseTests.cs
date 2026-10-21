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
}

using Basalt.Application.Sync;
using Basalt.Application.Vaults;
using Basalt.Domain.Notes;
using Basalt.Domain.Vaults;
using Basalt.Testing.Fakes;

namespace Basalt.Application.Tests.Sync;

public sealed class ConflictUseCaseTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly VaultId Vault = new(Guid.NewGuid());

    private readonly InMemoryVaultFileSystem _files = new();
    private readonly VaultSessions _sessions = new();

    public async ValueTask InitializeAsync()
    {
        _sessions.Add(new VaultSession(Vault, "Notes", _files, VaultSettings.Default));
        await Task.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _sessions.DisposeAsync();

    [Fact]
    public async Task ListsConflictCopiesAndNotesWithMarkers()
    {
        var copy = VaultPath.Create("Notes/A (conflict 2026-10-21 120000 Device).md").Value;
        var marked = VaultPath.Create("Notes/B.md").Value;
        var clean = VaultPath.Create("Notes/C.md").Value;
        await _files.WriteAllBytesAsync(copy, "x"u8.ToArray(), null, Ct);
        await _files.WriteAllBytesAsync(marked, "<<<<<<< local\na\n=======\nb\n>>>>>>> remote\n"u8.ToArray(), null, Ct);
        await _files.WriteAllBytesAsync(clean, "c"u8.ToArray(), null, Ct);

        var result = await new ListConflictsHandler(_sessions).HandleAsync(new ListConflicts(Vault), Ct);

        result.Value.Should().HaveCount(2);
        result.Value.Should().Contain(entry => entry.Path == copy && entry.IsConflictCopy);
        result.Value.Should().Contain(entry => entry.Path == marked && entry.HasMarkers);
    }

    [Fact]
    public async Task KeepRemote_OfACopy_ReplacesTheOriginalAndDeletesTheCopy()
    {
        var original = VaultPath.Create("Notes/A.md").Value;
        var copy = VaultPath.Create("Notes/A (conflict 2026-10-21 120000 Device).md").Value;
        await _files.WriteAllBytesAsync(original, "local"u8.ToArray(), null, Ct);
        await _files.WriteAllBytesAsync(copy, "remote"u8.ToArray(), null, Ct);

        var result = await new ResolveConflictHandler(_sessions)
            .HandleAsync(new ResolveConflict(Vault, copy, ConflictResolutionKind.KeepRemote), Ct);

        result.IsSuccess.Should().BeTrue();
        (await _files.ReadAllBytesAsync(original, Ct)).Value.Should().Equal("remote"u8.ToArray());
        (await _files.StatAsync(copy, Ct)).IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task KeepLocal_OfAMarkerNote_RemovesTheMarkers()
    {
        var path = VaultPath.Create("Notes/B.md").Value;
        await _files.WriteAllBytesAsync(path, "<<<<<<< local\na\n=======\nb\n>>>>>>> remote\n"u8.ToArray(), null, Ct);

        var result = await new ResolveConflictHandler(_sessions)
            .HandleAsync(new ResolveConflict(Vault, path, ConflictResolutionKind.KeepLocal), Ct);

        result.IsSuccess.Should().BeTrue();
        var text = (await _files.ReadAllTextAsync(path, Ct)).Value;
        text.Should().Contain("a").And.NotContain("b").And.NotContain("<<<<<<<");
    }

    [Fact]
    public async Task KeepBoth_LeavesTheCopyInPlace()
    {
        var copy = VaultPath.Create("Notes/A (conflict 2026-10-21 120000 Device).md").Value;
        await _files.WriteAllBytesAsync(copy, "remote"u8.ToArray(), null, Ct);

        var result = await new ResolveConflictHandler(_sessions)
            .HandleAsync(new ResolveConflict(Vault, copy, ConflictResolutionKind.KeepBoth), Ct);

        result.IsSuccess.Should().BeTrue();
        (await _files.StatAsync(copy, Ct)).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Merged_WritesTheTextAndDeletesTheCopy()
    {
        var original = VaultPath.Create("Notes/A.md").Value;
        var copy = VaultPath.Create("Notes/A (conflict 2026-10-21 120000 Device).md").Value;
        await _files.WriteAllBytesAsync(original, "local"u8.ToArray(), null, Ct);
        await _files.WriteAllBytesAsync(copy, "remote"u8.ToArray(), null, Ct);

        var result = await new ResolveConflictHandler(_sessions)
            .HandleAsync(new ResolveConflict(Vault, copy, ConflictResolutionKind.Merged, "merged"), Ct);

        result.IsSuccess.Should().BeTrue();
        (await _files.ReadAllTextAsync(original, Ct)).Value.Should().Be("merged");
        (await _files.StatAsync(copy, Ct)).IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task Merged_WithoutText_IsAnError()
    {
        var path = VaultPath.Create("Notes/A.md").Value;

        var result = await new ResolveConflictHandler(_sessions)
            .HandleAsync(new ResolveConflict(Vault, path, ConflictResolutionKind.Merged), Ct);

        result.Error.Code.Should().Be(SyncErrors.MissingMergedText);
    }

    [Fact]
    public async Task KeepRemote_OfAMarkerNote_KeepsTheRemoteSide()
    {
        var path = VaultPath.Create("Notes/B.md").Value;
        await _files.WriteAllBytesAsync(path, "<<<<<<< local\na\n=======\nb\n>>>>>>> remote\n"u8.ToArray(), null, Ct);

        var result = await new ResolveConflictHandler(_sessions)
            .HandleAsync(new ResolveConflict(Vault, path, ConflictResolutionKind.KeepRemote), Ct);

        result.IsSuccess.Should().BeTrue();
        var text = (await _files.ReadAllTextAsync(path, Ct)).Value;
        text.Should().Contain("b").And.NotContain("a").And.NotContain("<<<<<<<");
    }

    [Fact]
    public async Task KeepLocal_OfACopy_DeletesTheCopy()
    {
        var copy = VaultPath.Create("Notes/A (conflict 2026-10-21 120000 Device).md").Value;
        await _files.WriteAllBytesAsync(copy, "remote"u8.ToArray(), null, Ct);

        var result = await new ResolveConflictHandler(_sessions)
            .HandleAsync(new ResolveConflict(Vault, copy, ConflictResolutionKind.KeepLocal), Ct);

        result.IsSuccess.Should().BeTrue();
        (await _files.StatAsync(copy, Ct)).IsFailure.Should().BeTrue();
    }
    [Fact]
    public async Task UnknownVault_ReturnsNotOpen()
    {
        var result = await new ListConflictsHandler(_sessions)
            .HandleAsync(new ListConflicts(new VaultId(Guid.NewGuid())), Ct);

        result.Error.Code.Should().Be(Basalt.Application.Errors.ErrorCodes.VaultNotOpen);
    }
}

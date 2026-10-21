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
    public async Task UnknownVault_ReturnsNotOpen()
    {
        var result = await new ListConflictsHandler(_sessions)
            .HandleAsync(new ListConflicts(new VaultId(Guid.NewGuid())), Ct);

        result.Error.Code.Should().Be(Basalt.Application.Errors.ErrorCodes.VaultNotOpen);
    }
}

using Basalt.Application.Errors;
using Basalt.Application.Vaults;
using Basalt.Domain.Vaults;
using Basalt.Testing.Fakes;

namespace Basalt.Application.Tests.Vaults;

public sealed class VaultSessionsTests
{
    private static readonly VaultId Id = new(Guid.Parse("00000000-0000-0000-0000-00000000000a"));

    private sealed class Resource : IAsyncDisposable
    {
        public bool Disposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task AddGetClose_DisposesOwnedResources()
    {
        await using var sessions = new VaultSessions();
        var session = new VaultSession(Id, "Notas", new InMemoryVaultFileSystem(), VaultSettings.Default);
        var resource = new Resource();
        session.Own(resource);

        sessions.Add(session).IsSuccess.Should().BeTrue();
        sessions.Add(session).Error.Code.Should().Be(ErrorCodes.VaultAlreadyOpen);
        sessions.Get(Id).Value.Should().BeSameAs(session);
        sessions.All.Should().ContainSingle();

        await sessions.CloseAsync(Id);
        await sessions.CloseAsync(Id);

        resource.Disposed.Should().BeTrue();
        sessions.Get(Id).Error.Code.Should().Be(ErrorCodes.VaultNotOpen);
    }

    [Fact]
    public async Task UpdateSettings_ReplacesSettings()
    {
        await using var session = new VaultSession(Id, "Notas", new InMemoryVaultFileSystem(), VaultSettings.Default);

        session.UpdateSettings(VaultSettings.Default with { AttachmentFolder = "img" });

        session.Settings.AttachmentFolder.Should().Be("img");
        FluentActions.Invoking(() => session.UpdateSettings(null!)).Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task DisposingTheRegistry_ClosesEverything()
    {
        var sessions = new VaultSessions();
        var resource = new Resource();
        var session = new VaultSession(Id, "Notas", new InMemoryVaultFileSystem(), VaultSettings.Default);
        session.Own(resource);
        sessions.Add(session);

        await sessions.DisposeAsync();

        resource.Disposed.Should().BeTrue();
        sessions.All.Should().BeEmpty();
    }
}

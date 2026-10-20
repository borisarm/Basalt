using System.Text;
using Basalt.Application.Errors;
using Basalt.Application.Events;
using Basalt.Application.Ports.Storage;
using Basalt.Application.Vaults;
using Basalt.Domain.Common;
using Basalt.Domain.Notes;
using Basalt.Domain.Vaults;
using Basalt.Testing.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Basalt.Application.Tests.Vaults;

public sealed class VaultUseCasesTests : IAsyncDisposable
{
    private readonly InMemoryVaultFileSystemFactory _fileSystems = new();
    private readonly InMemoryVaultConfigStore _configs = new();
    private readonly VaultRegistry _registry = new(new InMemoryStore<VaultRegistryDocument>(() => VaultRegistryDocument.Empty));
    private readonly VaultSessions _sessions = new();
    private readonly InMemoryVaultLocalDataStore _localData = new();
    private readonly InMemoryEventBus _events = new(NullLogger<InMemoryEventBus>.Instance);
    private readonly SequentialIdGenerator _ids = new();
    private readonly FakeClock _clock = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private CreateVaultHandler Create => new(_fileSystems, _configs, _registry, _ids, _clock);

    private OpenFolderAsVaultHandler OpenFolder => new(_fileSystems, _configs, _registry, _ids, _clock);

    private OpenVaultHandler Open(params IVaultSessionInitializer[] initializers) => new(_registry, _fileSystems, _configs, _sessions, initializers, _events, _clock);

    public async ValueTask DisposeAsync()
    {
        await _sessions.DisposeAsync();
        await _events.DisposeAsync();
        _registry.Dispose();
    }

    [Fact]
    [Trait("Spec", "F-001.1")]
    public async Task CreateVault_CreatesFolderConfigAndRegistration_ThenOpens()
    {
        var created = await Create.HandleAsync(new CreateVault("Notas", "/home"), Ct);

        created.IsSuccess.Should().BeTrue();
        created.Value.RootLocation.Should().Be("/home/Notas");
        var fs = _fileSystems.Get("/home/Notas")!;
        var config = await _configs.LoadVaultConfigAsync(fs, Ct);
        config.Value!.Id.Should().Be(created.Value.Id);
        (await _registry.GetAllAsync(Ct)).Should().ContainSingle().Which.Name.Should().Be("Notas");

        var opened = await Open().HandleAsync(new OpenVault(created.Value.Id), Ct);
        opened.Value.Name.Should().Be("Notas");
        _sessions.Get(created.Value.Id).IsSuccess.Should().BeTrue();
    }

    [Fact]
    [Trait("Spec", "F-001.2")]
    public async Task CreateVault_InvalidName_CreatesNothing()
    {
        var result = await Create.HandleAsync(new CreateVault("a/b", "/home"), Ct);

        result.Error.Code.Should().Be(DomainErrors.VaultInvalidName);
        _fileSystems.Get("/home/a/b").Should().BeNull();
        (await _registry.GetAllAsync(Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task CreateVault_IntoNonEmptyFolder_Fails()
    {
        var existing = _fileSystems.AddFolder("/home/Notas");
        await existing.WriteAllBytesAsync(VaultPath.Create("x.md").Value, Encoding.UTF8.GetBytes("x"), null, Ct);

        (await Create.HandleAsync(new CreateVault("Notas", "/home"), Ct)).Error.Code.Should().Be(ErrorCodes.VaultLocationExists);
    }

    [Fact]
    [Trait("Spec", "F-001.3")]
    public async Task OpenFolderAsVault_WithoutConfig_CreatesIt()
    {
        var fs = _fileSystems.AddFolder("/docs/Mis notas");
        await fs.WriteAllBytesAsync(VaultPath.Create("a.md").Value, Encoding.UTF8.GetBytes("# A"), null, Ct);

        var result = await OpenFolder.HandleAsync(new OpenFolderAsVault("/docs/Mis notas"), Ct);

        result.Value.Name.Should().Be("Mis notas");
        (await _configs.LoadVaultConfigAsync(fs, Ct)).Value!.Id.Should().Be(result.Value.Id);
    }

    [Fact]
    [Trait("Spec", "F-001.4")]
    public async Task OpenFolderAsVault_FromAnotherDevice_KeepsItsId()
    {
        var fs = _fileSystems.AddFolder("/docs/Shared");
        var foreignId = new VaultId(Guid.Parse("11111111-2222-3333-4444-555555555555"));
        await _configs.SaveVaultConfigAsync(fs, new VaultConfig(foreignId, VaultSettings.Default), Ct);

        var result = await OpenFolder.HandleAsync(new OpenFolderAsVault("/docs/Shared"), Ct);

        result.Value.Id.Should().Be(foreignId);
    }

    [Fact]
    public async Task OpenFolderAsVault_SameVaultTwice_ReturnsTheRegistration_AndFollowsMoves()
    {
        var fs = _fileSystems.AddFolder("/a/V");
        var first = await OpenFolder.HandleAsync(new OpenFolderAsVault("/a/V"), Ct);
        var again = await OpenFolder.HandleAsync(new OpenFolderAsVault("/a/V"), Ct);

        var config = (await _configs.LoadVaultConfigAsync(fs, Ct)).Value!;
        var movedFs = _fileSystems.AddFolder("/b/V");
        await _configs.SaveVaultConfigAsync(movedFs, config, Ct);
        var moved = await OpenFolder.HandleAsync(new OpenFolderAsVault("/b/V"), Ct);

        again.Value.Should().Be(first.Value);
        moved.Value.Id.Should().Be(first.Value.Id);
        (await _registry.GetAllAsync(Ct)).Should().ContainSingle().Which.RootLocation.Should().Be("/b/V");
    }

    [Fact]
    public async Task OpenFolderAsVault_UnreadableConfig_IsNotOverwritten()
    {
        var fs = _fileSystems.AddFolder("/docs/Broken");
        _configs.Corrupt(fs);

        (await OpenFolder.HandleAsync(new OpenFolderAsVault("/docs/Broken"), Ct)).Error.Code.Should().Be(ErrorCodes.VaultConfigInvalid);
        (await OpenFolder.HandleAsync(new OpenFolderAsVault("/nope"), Ct)).Error.Code.Should().Be(ErrorCodes.VaultLocationMissing);
    }

    [Fact]
    [Trait("Spec", "F-001.5")]
    public async Task RemoveFromList_KeepsFilesAndDeletesLocalData()
    {
        var created = (await Create.HandleAsync(new CreateVault("Notas", "/home"), Ct)).Value;
        await Open().HandleAsync(new OpenVault(created.Id), Ct);
        var fs = _fileSystems.Get("/home/Notas")!;
        await fs.WriteAllBytesAsync(VaultPath.Create("keep.md").Value, Encoding.UTF8.GetBytes("x"), null, Ct);

        var result = await new RemoveVaultFromListHandler(_registry, _sessions, _localData, _events).HandleAsync(new RemoveVaultFromList(created.Id), Ct);

        result.IsSuccess.Should().BeTrue();
        (await _registry.GetAllAsync(Ct)).Should().BeEmpty();
        _sessions.Get(created.Id).IsFailure.Should().BeTrue();
        _localData.Deleted.Should().Equal(created.Id);
        (await fs.ExistsAsync(VaultPath.Create("keep.md").Value, Ct)).Should().BeTrue();
        (await fs.ExistsAsync(VaultPath.Create(".basalt/vault.json").Value, Ct)).Should().BeFalse("the in-memory config store does not write files");
    }

    [Fact]
    [Trait("Spec", "F-001.6")]
    public async Task OpenVault_MissingFolder_IsLocationMissing()
    {
        var created = (await Create.HandleAsync(new CreateVault("Notas", "/home"), Ct)).Value;
        _fileSystems.RemoveFolder("/home/Notas");

        var result = await Open().HandleAsync(new OpenVault(created.Id), Ct);

        result.Error.Code.Should().Be(ErrorCodes.VaultLocationMissing);
        result.Error.Metadata!["location"].Should().Be("/home/Notas");
    }

    [Fact]
    public async Task OpenVault_UnknownId_IsNotRegistered()
    {
        (await Open().HandleAsync(new OpenVault(new VaultId(Guid.Parse("00000000-0000-0000-0000-0000000000ff"))), Ct)).Error.Code.Should().Be(ErrorCodes.VaultNotRegistered);
    }

    [Fact]
    public async Task OpenVault_Twice_ReusesTheSession_AndRecordsLastOpened()
    {
        var created = (await Create.HandleAsync(new CreateVault("Notas", "/home"), Ct)).Value;

        await Open().HandleAsync(new OpenVault(created.Id), Ct);
        var second = await Open().HandleAsync(new OpenVault(created.Id), Ct);

        second.IsSuccess.Should().BeTrue();
        _sessions.All.Should().ContainSingle();
        (await _registry.GetAllAsync(Ct)).Single().LastOpenedAt.Should().Be(_clock.UtcNow);
    }

    [Fact]
    public async Task OpenVault_UnreadableConfig_OpensWithDefaultsAndWarning()
    {
        var created = (await Create.HandleAsync(new CreateVault("Notas", "/home"), Ct)).Value;
        _configs.Corrupt(_fileSystems.Get("/home/Notas")!);

        var opened = await Open().HandleAsync(new OpenVault(created.Id), Ct);

        opened.Value.ConfigWarning.Should().NotBeNull();
        opened.Value.Settings.Should().Be(VaultSettings.Default);
    }

    [Fact]
    public async Task OpenVault_MissingConfig_IsRecreatedWithTheRegisteredId()
    {
        var fs = _fileSystems.AddFolder("/x/V");
        var registration = new VaultRegistration(new VaultId(Guid.Parse("00000000-0000-0000-0000-0000000000aa")), "V", "/x/V", _clock.UtcNow);
        await _registry.AddAsync(registration, Ct);

        (await Open().HandleAsync(new OpenVault(registration.Id), Ct)).IsSuccess.Should().BeTrue();

        (await _configs.LoadVaultConfigAsync(fs, Ct)).Value!.Id.Should().Be(registration.Id);
    }

    [Fact]
    public async Task OpenVault_FailingInitializer_AbortsAndDisposes()
    {
        var created = (await Create.HandleAsync(new CreateVault("Notas", "/home"), Ct)).Value;
        var failing = new FailingInitializer();

        var result = await Open(failing).HandleAsync(new OpenVault(created.Id), Ct);

        result.Error.Code.Should().Be("test.init");
        _sessions.All.Should().BeEmpty();
    }

    [Fact]
    public async Task CloseVault_AndListVaults()
    {
        var a = (await Create.HandleAsync(new CreateVault("A", "/v"), Ct)).Value;
        var b = (await Create.HandleAsync(new CreateVault("B", "/v"), Ct)).Value;
        await Open().HandleAsync(new OpenVault(a.Id), Ct);

        var listed = (await new ListVaultsHandler(_registry, _sessions).HandleAsync(new ListVaults(), Ct)).Value;
        listed.Select(i => (i.Registration.Name, i.IsOpen)).Should().BeEquivalentTo([("A", true), ("B", false)]);

        (await new CloseVaultHandler(_sessions, _events).HandleAsync(new CloseVault(a.Id), Ct)).IsSuccess.Should().BeTrue();
        (await new CloseVaultHandler(_sessions, _events).HandleAsync(new CloseVault(a.Id), Ct)).IsSuccess.Should().BeTrue();
        _sessions.All.Should().BeEmpty();
        b.Should().NotBeNull();
    }

    [Fact]
    public async Task UpdateVaultSettings_SavesConfigAndSession()
    {
        var created = (await Create.HandleAsync(new CreateVault("Notas", "/home"), Ct)).Value;
        var handler = new UpdateVaultSettingsHandler(_sessions, _configs, _events);
        var settings = VaultSettings.Default with { NewNoteFolder = "Inbox" };

        (await handler.HandleAsync(new UpdateVaultSettings(created.Id, settings), Ct)).Error.Code.Should().Be(ErrorCodes.VaultNotOpen);
        await Open().HandleAsync(new OpenVault(created.Id), Ct);
        (await handler.HandleAsync(new UpdateVaultSettings(created.Id, settings), Ct)).IsSuccess.Should().BeTrue();

        _sessions.Get(created.Id).Value.Settings.NewNoteFolder.Should().Be("Inbox");
        (await _configs.LoadVaultConfigAsync(_fileSystems.Get("/home/Notas")!, Ct)).Value!.Settings.NewNoteFolder.Should().Be("Inbox");
    }

    [Fact]
    public async Task StorageFailures_ArePropagated()
    {
        var failingStore = new InMemoryStore<VaultRegistryDocument>(() => VaultRegistryDocument.Empty) { FailNextSave = new Error(ErrorCodes.StorageIo, "disk") };
        using var registry = new VaultRegistry(failingStore);
        var create = new CreateVaultHandler(_fileSystems, _configs, registry, _ids, _clock);

        (await create.HandleAsync(new CreateVault("Notas", "/home"), Ct)).Error.Code.Should().Be(ErrorCodes.StorageIo);

        _fileSystems.AddFolder("/f/V");
        failingStore.FailNextSave = new Error(ErrorCodes.StorageIo, "disk");
        var openFolder = new OpenFolderAsVaultHandler(_fileSystems, _configs, registry, _ids, _clock);
        (await openFolder.HandleAsync(new OpenFolderAsVault("/f/V"), Ct)).Error.Code.Should().Be(ErrorCodes.StorageIo);

        var remove = new RemoveVaultFromListHandler(registry, _sessions, _localData, _events);
        (await remove.HandleAsync(new RemoveVaultFromList(new VaultId(Guid.Parse("00000000-0000-0000-0000-0000000000ff"))), Ct)).Error.Code.Should().Be(ErrorCodes.VaultNotRegistered);
        await FluentActions.Awaiting(() => create.HandleAsync(null!, Ct)).Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task ListVaults_OrdersByLastOpened()
    {
        var a = (await Create.HandleAsync(new CreateVault("A", "/v"), Ct)).Value;
        _clock.Advance(TimeSpan.FromMinutes(1));
        await Create.HandleAsync(new CreateVault("B", "/v"), Ct);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await Open().HandleAsync(new OpenVault(a.Id), Ct);

        var listed = (await new ListVaultsHandler(_registry, _sessions).HandleAsync(new ListVaults(), Ct)).Value;

        listed.Select(i => i.Registration.Name).Should().Equal("A", "B");
    }

    private sealed class FailingInitializer : IVaultSessionInitializer
    {
        public Task<Result<Unit>> InitializeAsync(VaultSession session, CancellationToken ct) =>
            Task.FromResult(Result.Failure(new Error("test.init", "boom")));
    }

    private RelocateVaultHandler Relocate => new(_registry, _fileSystems, _configs);

    [Fact]
    [Trait("Spec", "F-001.6")]
    public async Task Relocate_PointsTheRegistrationAtTheMovedFolder()
    {
        var created = (await Create.HandleAsync(new CreateVault("Notas", "/home"), Ct)).Value;
        var moved = (await _fileSystems.CreateAsync("/otro/Notas", Ct)).Value;
        await _configs.SaveVaultConfigAsync(moved, new VaultConfig(created.Id, VaultSettings.Default), Ct);

        var result = await Relocate.HandleAsync(new RelocateVault(created.Id, "/otro/Notas"), Ct);

        result.IsSuccess.Should().BeTrue();
        (await _registry.GetAllAsync(Ct)).Single().RootLocation.Should().Be("/otro/Notas");
    }

    [Fact]
    public async Task Relocate_ToAFolderWithoutConfig_IsAccepted_AndToAnotherVault_IsRefused()
    {
        var created = (await Create.HandleAsync(new CreateVault("Notas", "/home"), Ct)).Value;
        await _fileSystems.CreateAsync("/sin-config", Ct);
        var other = (await _fileSystems.CreateAsync("/otro-vault", Ct)).Value;
        await _configs.SaveVaultConfigAsync(other, new VaultConfig(new VaultId(Guid.Parse("99999999-0000-4000-8000-000000000000")), VaultSettings.Default), Ct);

        (await Relocate.HandleAsync(new RelocateVault(created.Id, "/otro-vault"), Ct)).Error.Code.Should().Be(ErrorCodes.VaultIdMismatch);
        (await Relocate.HandleAsync(new RelocateVault(created.Id, "/no-existe"), Ct)).Error.Code.Should().Be(ErrorCodes.VaultLocationMissing);
        (await Relocate.HandleAsync(new RelocateVault(new VaultId(Guid.Parse("88888888-0000-4000-8000-000000000000")), "/sin-config"), Ct)).Error.Code.Should().Be(ErrorCodes.VaultNotRegistered);
        (await Relocate.HandleAsync(new RelocateVault(created.Id, "/sin-config"), Ct)).IsSuccess.Should().BeTrue();
    }
}

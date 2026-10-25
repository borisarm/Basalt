using Basalt.Application.Errors;
using Basalt.Application.Vaults;
using Basalt.Domain.Common;
using Basalt.Domain.Vaults;
using Basalt.Testing.Fakes;

namespace Basalt.Application.Tests.Vaults;

public sealed class VaultRegistryTests
{
    private static readonly DateTimeOffset When = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    private static VaultRegistration Registration(string name = "Notes") =>
        new(new VaultId(Guid.NewGuid()), name, "C:\\vault", When);

    private static VaultRegistry Registry(InMemoryStore<VaultRegistryDocument>? store = null) =>
        new(store ?? new InMemoryStore<VaultRegistryDocument>(() => VaultRegistryDocument.Empty));

    [Fact]
    public async Task Add_StoresTheVaultOnce()
    {
        using var registry = Registry();
        var registration = Registration();

        (await registry.AddAsync(registration, CancellationToken.None)).IsSuccess.Should().BeTrue();
        var duplicate = await registry.AddAsync(registration, CancellationToken.None);

        (await registry.GetAllAsync(CancellationToken.None)).Should().Equal(registration);
        duplicate.Error.Code.Should().Be(ErrorCodes.VaultAlreadyRegistered);
        duplicate.Error.Message.Should().Be("The vault is already registered.");
    }

    [Fact]
    public async Task Update_ReplacesItAndReportsUnknownIds()
    {
        using var registry = Registry();
        var registration = Registration();
        await registry.AddAsync(registration, CancellationToken.None);
        var updated = registration with { Name = "Renamed", LastOpenedAt = When };

        (await registry.UpdateAsync(updated, CancellationToken.None)).IsSuccess.Should().BeTrue();
        var missing = await registry.UpdateAsync(Registration("Other"), CancellationToken.None);

        (await registry.GetAllAsync(CancellationToken.None)).Should().Equal(updated);
        missing.Error.Code.Should().Be(ErrorCodes.VaultNotRegistered);
        missing.Error.Message.Should().Be("The vault is not registered.");
    }

    [Fact]
    public async Task Remove_DeletesItAndReportsUnknownIds()
    {
        using var registry = Registry();
        var registration = Registration();
        await registry.AddAsync(registration, CancellationToken.None);

        (await registry.RemoveAsync(registration.Id, CancellationToken.None)).IsSuccess.Should().BeTrue();
        var missing = await registry.RemoveAsync(registration.Id, CancellationToken.None);

        (await registry.GetAllAsync(CancellationToken.None)).Should().BeEmpty();
        missing.Error.Code.Should().Be(ErrorCodes.VaultNotRegistered);
    }

    [Fact]
    public async Task SaveFailures_ArePropagated()
    {
        var failure = new Error(ErrorCodes.StorageIo, "disk");
        var store = new InMemoryStore<VaultRegistryDocument>(() => VaultRegistryDocument.Empty) { FailNextSave = failure };
        using var registry = Registry(store);

        var result = await registry.AddAsync(Registration(), CancellationToken.None);

        result.Error.Should().Be(failure);
    }
}

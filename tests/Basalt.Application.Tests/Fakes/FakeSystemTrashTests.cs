using Basalt.Application.Ports.FileSystem;
using Basalt.Application.Ports.Platform;
using Basalt.Testing.Contracts;
using Basalt.Testing.Fakes;

namespace Basalt.Application.Tests.Fakes;

public sealed class FakeSystemTrashTests : SystemTrashContract
{
    private readonly InMemoryVaultFileSystem _vault = new();

    protected override IVaultFileSystem Vault => _vault;

    protected override Task<ISystemTrash> CreateSutAsync() => Task.FromResult<ISystemTrash>(new FakeSystemTrash(_vault));
}

public sealed class UnavailableFakeSystemTrashTests : SystemTrashContract
{
    private readonly InMemoryVaultFileSystem _vault = new();

    protected override IVaultFileSystem Vault => _vault;

    protected override Task<ISystemTrash> CreateSutAsync() => Task.FromResult<ISystemTrash>(new FakeSystemTrash(_vault, available: false));
}

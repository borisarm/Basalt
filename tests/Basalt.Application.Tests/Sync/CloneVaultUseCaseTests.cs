using Basalt.Application.Ports.FileSystem;
using Basalt.Application.Ports.Remote;
using Basalt.Application.Ports.Storage;
using Basalt.Application.Sync;
using Basalt.Application.Vaults;
using Basalt.Domain.Accounts;
using Basalt.Domain.Common;
using Basalt.Domain.Notes;
using Basalt.Domain.Sync;
using Basalt.Testing.Fakes;
using NSubstitute;

namespace Basalt.Application.Tests.Sync;

public sealed class CloneVaultUseCaseTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly GitHubAccountId Account = new(Guid.NewGuid());
    private static readonly RemoteRepositoryRef Ref = new(GitHubHost.GitHubDotCom, "acme", "notes", "main");

    [Fact]
    public async Task EmptyRepository_IsAnError()
    {
        var handler = Handler(new InMemoryRemoteRepositoryFactory());

        var result = await handler.HandleAsync(Command(), Ct);

        result.Error.Code.Should().Be(RemoteErrors.RepositoryEmpty);
    }

    [Fact]
    public async Task Clone_UsesTheRemoteVaultIdAndSavesTheProfile()
    {
        var factory = new InMemoryRemoteRepositoryFactory();
        var remote = (InMemoryRemoteRepository)factory.Create(Account, Ref);
        var id = Guid.NewGuid();
        await remote.CreateInitialCommitAsync(
            VaultPath.Create(".basalt/vault.json").Value,
            System.Text.Encoding.UTF8.GetBytes($"{{\"vaultId\":\"{id:D}\"}}"),
            "init",
            Ct);
        var profiles = new InMemorySyncProfileStore();
        var handler = Handler(factory, profiles);

        var result = await handler.HandleAsync(Command(), Ct);

        result.IsSuccess.Should().BeTrue();
        result.Value.Id.Value.Should().Be(id);
        (await profiles.GetAsync(result.Value.Id, Ct)).Should().NotBeNull();
    }

    [Fact]
    public async Task InvalidName_IsRejected()
    {
        var handler = Handler(new InMemoryRemoteRepositoryFactory());

        var result = await handler.HandleAsync(new CloneVaultFromRepository(Account, Ref, "C:\\vaults", ".."), Ct);

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task ExistingFolder_IsRejectedAfterTheRemoteCheck()
    {
        var factory = new InMemoryRemoteRepositoryFactory();
        var remote = (InMemoryRemoteRepository)factory.Create(Account, Ref);
        await remote.CreateInitialCommitAsync(VaultPath.Create("A.md").Value, "a"u8.ToArray(), "init", Ct);
        var fileSystems = Substitute.For<IVaultFileSystemFactory>();
        fileSystems.Combine("C:\\vaults", "Notes").Returns("C:\\vaults\\Notes");
        fileSystems.CreateAsync("C:\\vaults\\Notes", Arg.Any<CancellationToken>())
            .Returns(Result<IVaultFileSystem>.Failure(new Error(Basalt.Application.Errors.ErrorCodes.FsAlreadyExists, "exists")));
        var handler = new CloneVaultFromRepositoryHandler(
            fileSystems,
            Substitute.For<IVaultConfigStore>(),
            Substitute.For<IVaultRegistry>(),
            new InMemorySyncProfileStore(),
            factory,
            new SequentialIdGenerator(),
            new FakeClock());

        var result = await handler.HandleAsync(Command(), Ct);

        result.Error.Code.Should().Be(Basalt.Application.Errors.ErrorCodes.VaultLocationExists);
    }

    [Fact]
    public async Task MissingRemoteConfig_GetsANewVaultId()
    {
        var factory = new InMemoryRemoteRepositoryFactory();
        var remote = (InMemoryRemoteRepository)factory.Create(Account, Ref);
        await remote.CreateInitialCommitAsync(VaultPath.Create("A.md").Value, "a"u8.ToArray(), "init", Ct);
        var handler = Handler(factory);

        var result = await handler.HandleAsync(Command(), Ct);

        result.IsSuccess.Should().BeTrue();
        result.Value.Id.Value.Should().NotBe(Guid.Empty);
    }
    [Fact]
    public async Task RegistryFailure_IsReturned()
    {
        var factory = new InMemoryRemoteRepositoryFactory();
        var remote = (InMemoryRemoteRepository)factory.Create(Account, Ref);
        await remote.CreateInitialCommitAsync(VaultPath.Create("A.md").Value, "a"u8.ToArray(), "init", Ct);
        var files = new InMemoryVaultFileSystem();
        var fileSystems = Substitute.For<IVaultFileSystemFactory>();
        fileSystems.Combine("C:\\vaults", "Notes").Returns("C:\\vaults\\Notes");
        fileSystems.CreateAsync("C:\\vaults\\Notes", Arg.Any<CancellationToken>())
            .Returns(Result<IVaultFileSystem>.Success(files));
        var configs = Substitute.For<IVaultConfigStore>();
        configs.SaveVaultConfigAsync(files, Arg.Any<VaultConfig>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        var registry = Substitute.For<IVaultRegistry>();
        registry.AddAsync(Arg.Any<VaultRegistration>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure(new Error("vault.already_registered", "exists")));
        var handler = new CloneVaultFromRepositoryHandler(
            fileSystems, configs, registry, new InMemorySyncProfileStore(), factory,
            new SequentialIdGenerator(), new FakeClock());

        var result = await handler.HandleAsync(Command(), Ct);

        result.Error.Code.Should().Be("vault.already_registered");
    }

    [Fact]
    public async Task RemoteTreeFailure_IsReturned()
    {
        var commit = CommitSha.Parse(new string("a"[0], 40)).Value;
        var tree = TreeSha.Parse(new string("b"[0], 40)).Value;
        var remote = Substitute.For<IRemoteRepository>();
        remote.GetHeadAsync(null, Arg.Any<CancellationToken>())
            .Returns(Result<RemoteHeadCheck>.Success(new RemoteHeadCheck(new RemoteHead(commit, tree), null, false)));
        remote.GetTreeAsync(tree, Arg.Any<CancellationToken>())
            .Returns(Result<RemoteTreeSnapshot>.Failure(new Error(RemoteErrors.ServerError, "boom")));
        var factory = Substitute.For<IRemoteRepositoryFactory>();
        factory.Create(Account, Ref).Returns(remote);
        var handler = Handler(factory);

        var result = await handler.HandleAsync(Command(), Ct);

        result.Error.Code.Should().Be(RemoteErrors.ServerError);
    }
    private static CloneVaultFromRepository Command() =>
        new(Account, Ref, "C:\\vaults", "Notes");

    private static CloneVaultFromRepositoryHandler Handler(
        IRemoteRepositoryFactory repositories,
        InMemorySyncProfileStore? profiles = null)
    {
        var files = new InMemoryVaultFileSystem();
        var fileSystems = Substitute.For<IVaultFileSystemFactory>();
        fileSystems.Combine("C:\\vaults", "Notes").Returns("C:\\vaults\\Notes");
        fileSystems.CreateAsync("C:\\vaults\\Notes", Arg.Any<CancellationToken>())
            .Returns(Result<IVaultFileSystem>.Success(files));
        var configs = Substitute.For<IVaultConfigStore>();
        configs.SaveVaultConfigAsync(files, Arg.Any<VaultConfig>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        var registry = Substitute.For<IVaultRegistry>();
        registry.AddAsync(Arg.Any<VaultRegistration>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        return new CloneVaultFromRepositoryHandler(
            fileSystems, configs, registry, profiles ?? new InMemorySyncProfileStore(), repositories,
            new SequentialIdGenerator(), new FakeClock());
    }
}

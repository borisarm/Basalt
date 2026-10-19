using Basalt.Application.Accounts;
using Basalt.Application.Errors;
using Basalt.Application.Settings;
using Basalt.Application.Vaults;
using Basalt.Domain.Accounts;
using Basalt.Domain.Common;
using Basalt.Domain.Vaults;
using Basalt.Testing.Fakes;

namespace Basalt.Application.Tests.Storage;

public sealed class StoreServicesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static VaultRegistration Reg(int n, string name = "V") =>
        new(new VaultId(new Guid($"00000000-0000-0000-0000-{n:D12}")), name, $"/vaults/{n}", DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task VaultRegistry_AddUpdateRemove()
    {
        using var registry = new VaultRegistry(new InMemoryStore<VaultRegistryDocument>(() => VaultRegistryDocument.Empty));

        (await registry.AddAsync(Reg(1), Ct)).IsSuccess.Should().BeTrue();
        (await registry.AddAsync(Reg(2), Ct)).IsSuccess.Should().BeTrue();
        (await registry.AddAsync(Reg(1), Ct)).Error.Code.Should().Be(ErrorCodes.VaultAlreadyRegistered);
        (await registry.UpdateAsync(Reg(2, "Renamed"), Ct)).IsSuccess.Should().BeTrue();
        (await registry.UpdateAsync(Reg(9), Ct)).Error.Code.Should().Be(ErrorCodes.VaultNotRegistered);
        (await registry.RemoveAsync(Reg(1).Id, Ct)).IsSuccess.Should().BeTrue();
        (await registry.RemoveAsync(Reg(1).Id, Ct)).Error.Code.Should().Be(ErrorCodes.VaultNotRegistered);

        (await registry.GetAllAsync(Ct)).Should().Equal(Reg(2, "Renamed"));
    }

    [Fact]
    public async Task VaultRegistry_ConcurrentAdds_AreAllKept()
    {
        using var registry = new VaultRegistry(new InMemoryStore<VaultRegistryDocument>(() => VaultRegistryDocument.Empty));

        await Task.WhenAll(Enumerable.Range(1, 30).Select(i => registry.AddAsync(Reg(i), Ct)));

        (await registry.GetAllAsync(Ct)).Should().HaveCount(30);
    }

    [Fact]
    public async Task VaultRegistry_SaveFailure_IsReturned()
    {
        var store = new InMemoryStore<VaultRegistryDocument>(() => VaultRegistryDocument.Empty) { FailNextSave = new Error(ErrorCodes.StorageIo, "disk") };
        using var registry = new VaultRegistry(store);

        (await registry.AddAsync(Reg(1), Ct)).Error.Code.Should().Be(ErrorCodes.StorageIo);
        (await registry.GetAllAsync(Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task AccountStore_SaveGetRemove()
    {
        using var accounts = new AccountStore(new InMemoryStore<AccountsDocument>(() => AccountsDocument.Empty));
        var id = new GitHubAccountId(Guid.Parse("00000000-0000-0000-0000-00000000000a"));
        var account = new GitHubAccount(id, "boris", GitHubHost.GitHubDotCom, null, ["repo"], DateTimeOffset.UnixEpoch);

        (await accounts.GetAsync(id, Ct)).Error.Code.Should().Be(ErrorCodes.AccountNotFound);
        await accounts.SaveAsync(account, Ct);
        await accounts.SaveAsync(account with { Login = "boris2" }, Ct);

        (await accounts.GetAllAsync(Ct)).Should().ContainSingle().Which.Login.Should().Be("boris2");
        (await accounts.GetAsync(id, Ct)).Value.Login.Should().Be("boris2");
        (await accounts.RemoveAsync(id, Ct)).IsSuccess.Should().BeTrue();
        (await accounts.RemoveAsync(id, Ct)).Error.Code.Should().Be(ErrorCodes.AccountNotFound);
        await FluentActions.Awaiting(() => accounts.SaveAsync(null!, Ct)).Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task AppSettingsStore_DefaultsAndSave()
    {
        var store = new AppSettingsStore(new InMemoryStore<AppSettings>(() => AppSettings.Default));

        (await store.GetAsync(Ct)).Should().Be(AppSettings.Default);
        await store.SaveAsync(AppSettings.Default with { Theme = ThemeMode.Dark, Language = "es" }, Ct);

        (await store.GetAsync(Ct)).Theme.Should().Be(ThemeMode.Dark);
        AppSettings.Default.Editor.WordWrap.Should().BeTrue();
        AppSettings.Default.OpenLastVault.Should().BeTrue();
    }
}

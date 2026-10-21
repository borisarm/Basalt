using Basalt.Application.Ports.Remote;
using Basalt.Application.Ports.Storage;
using Basalt.Application.Sync;
using Basalt.Domain.Accounts;
using Basalt.Domain.Sync;
using Basalt.Domain.Vaults;

namespace Basalt.Testing.Contracts;

/// <summary>Behaviour of <see cref="ISyncProfileStore"/> (spec 03 §1.6 and 06 §2).</summary>
public abstract class SyncProfileStoreContract : ContractTest<ISyncProfileStore>
{
    private static readonly VaultId Vault = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly VaultId Other = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));

    protected static SyncProfile Profile(VaultId vault) => new(
        vault,
        new GitHubAccountId(Guid.Parse("33333333-3333-3333-3333-333333333333")),
        new RemoteRepositoryRef(GitHubHost.GitHubDotCom, "owner", "repo", "main"),
        Enabled: true,
        Paused: false,
        new SyncTriggers(
            OnVaultOpen: false,
            Interval: TimeSpan.FromMinutes(15),
            AfterEditIdle: TimeSpan.FromSeconds(30),
            OnAppBackground: true,
            BackgroundRefresh: false,
            OnlyOnUnmeteredNetwork: true),
        new SyncPolicy(
            TextConflictPolicy.AutoMergeOrMarkers,
            BinaryConflictPolicy.PreferRemote,
            DeleteConflictPolicy.PreferDelete,
            MaxFileSizeBytes: 1024),
        new SyncScope(
            IncludeAttachments: false,
            IncludeObsidianConfig: true,
            IncludeTrash: true,
            ExtraIgnorePatterns: ["*.tmp", "drafts/"]),
        new CommitSettings("Sync {summary}", "Basalt", "basalt@example.com"));

    [Fact]
    public async Task MissingProfile_ReturnsNull()
    {
        (await Sut.GetAsync(Vault, Ct)).Should().BeNull();
    }

    [Fact]
    public async Task SavedProfile_RoundTripsEveryField()
    {
        var profile = Profile(Vault);

        (await Sut.SaveAsync(profile, Ct)).IsSuccess.Should().BeTrue();
        (await Sut.GetAsync(Vault, Ct)).Should().BeEquivalentTo(profile);
    }

    [Fact]
    public async Task SavingAgain_ReplacesTheProfile()
    {
        await Sut.SaveAsync(Profile(Vault), Ct);
        var updated = Profile(Vault) with { Paused = true, Enabled = false };

        await Sut.SaveAsync(updated, Ct);

        (await Sut.GetAsync(Vault, Ct)).Should().BeEquivalentTo(updated);
    }

    [Fact]
    public async Task ProfilesAreIsolatedPerVault()
    {
        await Sut.SaveAsync(Profile(Vault), Ct);
        await Sut.SaveAsync(Profile(Other) with { Repository = new RemoteRepositoryRef(GitHubHost.GitHubDotCom, "other", "repo", "trunk") }, Ct);

        (await Sut.GetAsync(Vault, Ct))!.Repository.Owner.Should().Be("owner");
        (await Sut.GetAsync(Other, Ct))!.Repository.Owner.Should().Be("other");
    }

    [Fact]
    public async Task Delete_RemovesOnlyThatVaultAndIsIdempotent()
    {
        await Sut.SaveAsync(Profile(Vault), Ct);
        await Sut.SaveAsync(Profile(Other), Ct);

        (await Sut.DeleteAsync(Vault, Ct)).IsSuccess.Should().BeTrue();
        (await Sut.DeleteAsync(Vault, Ct)).IsSuccess.Should().BeTrue();

        (await Sut.GetAsync(Vault, Ct)).Should().BeNull();
        (await Sut.GetAsync(Other, Ct)).Should().NotBeNull();
    }
}

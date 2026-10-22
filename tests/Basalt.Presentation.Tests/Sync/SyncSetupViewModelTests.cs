using Basalt.Application.Abstractions;
using Basalt.Application.Accounts;
using Basalt.Application.Ports.Remote;
using Basalt.Application.Sync;
using Basalt.Domain.Accounts;
using Basalt.Domain.Common;
using Basalt.Domain.Vaults;
using Basalt.Presentation.Common;
using Basalt.Presentation.Sync;
using Basalt.Testing.Fakes;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Basalt.Presentation.Tests.Sync;

public sealed class SyncSetupViewModelTests
{
    private static readonly GitHubAccountId Account = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;
    private static readonly VaultId Vault = new(Guid.NewGuid());

    private readonly FakeNotificationService _notifications = new();

    private ViewModelServices Services() =>
        new(new ImmediateDispatcher(), _notifications, PresentationFakes.Localizer(), new FakeTimeProvider(Now));

    [Fact]
    public async Task Configure_PreviewAndConfirm()
    {
        var preview = Substitute.For<IQueryHandler<PreviewSyncLink, SyncLinkPreview>>();
        var configure = Substitute.For<ICommandHandler<ConfigureSync, SyncLinkPreview>>();
        var value = new SyncLinkPreview(SyncLinkMode.Merge, 1, 1, 1, 1, false, true, false);
        preview.HandleAsync(Arg.Any<PreviewSyncLink>(), Arg.Any<CancellationToken>())
            .Returns(Result<SyncLinkPreview>.Success(value));
        configure.HandleAsync(Arg.Any<ConfigureSync>(), Arg.Any<CancellationToken>())
            .Returns(Result<SyncLinkPreview>.Success(value));
        var vm = new ConfigureSyncWizardViewModel(Services(), new ConfigureSyncUseCases(preview, configure));
        vm.Attach(Vault, Account);
        vm.Owner = "acme";
        vm.RepositoryName = "notes";

        await vm.PreviewAsync();
        await vm.ConfigureAsync();

        vm.Preview!.Mode.Should().Be(SyncLinkMode.Merge);
        vm.IsMerge.Should().BeTrue();
        vm.CanConfirm.Should().BeTrue();
        await configure.Received(1).HandleAsync(Arg.Any<ConfigureSync>(), Arg.Any<CancellationToken>());
        _notifications.Shown.Should().ContainSingle();
    }

    [Fact]
    [Trait("Spec", "F-015.2")]
    public async Task CreateRepository_LoadsAccountsAndPreviews()
    {
        var account = new GitHubAccount(Account, "acme", GitHubHost.GitHubDotCom, null, ["repo"], Now);
        var accounts = Substitute.For<IQueryHandler<ListAccounts, IReadOnlyList<GitHubAccount>>>();
        accounts.HandleAsync(Arg.Any<ListAccounts>(), Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<GitHubAccount>>.Success([account]));
        var create = Substitute.For<ICommandHandler<CreateRepositoryForVault, RemoteRepositoryInfo>>();
        create.HandleAsync(Arg.Any<CreateRepositoryForVault>(), Arg.Any<CancellationToken>())
            .Returns(Result<RemoteRepositoryInfo>.Success(new RemoteRepositoryInfo("acme", "notas-vault", "main", true, false, true)));
        var preview = Substitute.For<IQueryHandler<PreviewSyncLink, SyncLinkPreview>>();
        preview.HandleAsync(Arg.Any<PreviewSyncLink>(), Arg.Any<CancellationToken>())
            .Returns(Result<SyncLinkPreview>.Success(new SyncLinkPreview(SyncLinkMode.Upload, 0, 3, 0, 0, false, true, false)));
        var vm = new ConfigureSyncWizardViewModel(Services(), new ConfigureSyncUseCases(
            preview, Substitute.For<ICommandHandler<ConfigureSync, SyncLinkPreview>>(), accounts, create));
        vm.Attach(Vault, "Mis Notas");

        await vm.LoadAsync();
        await vm.CreateRepositoryAsync();

        vm.SelectedAccount.Should().Be(account);
        vm.Owner.Should().Be("acme");
        vm.RepositoryName.Should().Be("notas-vault");
        vm.Preview!.LocalOnly.Should().Be(3);
        await create.Received(1).HandleAsync(
            Arg.Is<CreateRepositoryForVault>(command => command.Name == "mis-notas" && command.Private), Arg.Any<CancellationToken>());
    }
}

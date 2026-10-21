using Basalt.Application.Abstractions;
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
}

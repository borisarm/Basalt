using Basalt.Application.Abstractions;
using Basalt.Application.Ports.Remote;
using Basalt.Application.Sync;
using Basalt.Domain.Accounts;
using Basalt.Domain.Common;
using Basalt.Domain.Sync;
using Basalt.Domain.Vaults;
using Basalt.Presentation.Common;
using Basalt.Presentation.Sync;
using Basalt.Testing.Fakes;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Basalt.Presentation.Tests.Sync;

public sealed class VaultSyncSettingsViewModelTests
{
    private static readonly VaultId Vault = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    [Fact]
    public async Task Load_ShowsRepositoryAndPauseResumeDisable()
    {
        var profiles = new InMemorySyncProfileStore();
        await profiles.SaveAsync(new SyncProfile(
            Vault,
            new GitHubAccountId(Guid.NewGuid()),
            new RemoteRepositoryRef(GitHubHost.GitHubDotCom, "acme", "notes", "main"),
            true,
            false,
            new SyncTriggers(OnVaultOpen: true, Interval: TimeSpan.FromMinutes(15)),
            SyncPolicy.Default,
            SyncScope.Default,
            new CommitSettings()), CancellationToken.None);
        var pause = Substitute.For<ICommandHandler<PauseSync, Unit>>();
        var resume = Substitute.For<ICommandHandler<ResumeSync, Unit>>();
        var disable = Substitute.For<ICommandHandler<DisableSync, Unit>>();
        pause.HandleAsync(Arg.Any<PauseSync>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        resume.HandleAsync(Arg.Any<ResumeSync>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        disable.HandleAsync(Arg.Any<DisableSync>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        var vm = new VaultSyncSettingsViewModel(
            new ViewModelServices(new ImmediateDispatcher(), new FakeNotificationService(), PresentationFakes.Localizer(), new FakeTimeProvider(Now)),
            profiles,
            new VaultSyncSettingsUseCases(pause, resume, disable));
        vm.Attach(Vault);

        await vm.LoadAsync();
        await vm.PauseCommand.ExecuteAsync(null);
        await vm.ResumeCommand.ExecuteAsync(null);
        await vm.DisableCommand.ExecuteAsync(null);

        vm.IsConfigured.Should().BeFalse();
        vm.IsPaused.Should().BeFalse();
        await disable.Received(1).HandleAsync(Arg.Any<DisableSync>(), Arg.Any<CancellationToken>());
    }
}

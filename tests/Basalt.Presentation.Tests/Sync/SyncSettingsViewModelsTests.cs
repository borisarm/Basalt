using Basalt.Application.Abstractions;
using Basalt.Application.Accounts;
using Basalt.Application.Sync;
using Basalt.Domain.Accounts;
using Basalt.Domain.Common;
using Basalt.Domain.Notes;
using Basalt.Domain.Vaults;
using Basalt.Presentation.Common;
using Basalt.Presentation.Sync;
using Basalt.Testing.Fakes;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Basalt.Presentation.Tests.Sync;

public sealed class SyncSettingsViewModelsTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;
    private static readonly VaultId Vault = new(Guid.NewGuid());

    private readonly FakeNotificationService _notifications = new();

    private ViewModelServices Services() =>
        new(new ImmediateDispatcher(), _notifications, PresentationFakes.Localizer(), new FakeTimeProvider(Now));

    [Fact]
    public async Task Accounts_LoadAndRemove()
    {
        var account = new GitHubAccount(new GitHubAccountId(Guid.NewGuid()), "octocat", GitHubHost.GitHubDotCom, null, ["repo"], Now);
        var list = Substitute.For<IQueryHandler<ListAccounts, IReadOnlyList<GitHubAccount>>>();
        var remove = Substitute.For<ICommandHandler<RemoveAccount, Unit>>();
        list.HandleAsync(Arg.Any<ListAccounts>(), Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<GitHubAccount>>.Success([account]));
        remove.HandleAsync(Arg.Any<RemoveAccount>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        var vm = new AccountsSettingsViewModel(Services(), new AccountsSettingsUseCases(list, remove));

        await vm.LoadAsync();
        vm.Selected = account;
        await vm.RemoveCommand.ExecuteAsync(null);

        vm.Accounts.Should().BeEmpty();
        vm.IsEmpty.Should().BeTrue();
        await remove.Received(1).HandleAsync(Arg.Any<RemoveAccount>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Conflicts_LoadAndResolve()
    {
        var conflict = new SyncConflictEntry(VaultPath.Create("Notes/A (conflict x).md").Value, true, false);
        var list = Substitute.For<IQueryHandler<ListConflicts, IReadOnlyList<SyncConflictEntry>>>();
        var resolve = Substitute.For<ICommandHandler<ResolveConflict, Unit>>();
        list.HandleAsync(Arg.Any<ListConflicts>(), Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<SyncConflictEntry>>.Success([conflict]));
        resolve.HandleAsync(Arg.Any<ResolveConflict>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        var vm = new ConflictCenterViewModel(Services(), new ConflictCenterUseCases(list, resolve));
        vm.Attach(Vault);
        await vm.ReloadAsync();
        vm.Selected = conflict;

        await vm.ResolveCommand.ExecuteAsync(null);

        vm.Conflicts.Should().BeEmpty();
        await resolve.Received(1).HandleAsync(Arg.Any<ResolveConflict>(), Arg.Any<CancellationToken>());
    }
}

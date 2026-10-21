using Basalt.Application.Abstractions;
using Basalt.Application.Ports.Remote;
using Basalt.Application.Sync;
using Basalt.Domain.Common;
using Basalt.Domain.Notes;
using Basalt.Domain.Sync;
using Basalt.Domain.Vaults;
using Basalt.Presentation.Common;
using Basalt.Presentation.Sync;
using Basalt.Testing.Fakes;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Basalt.Presentation.Tests.Sync;

public sealed class FileHistoryViewModelTests
{
    private static readonly VaultId Vault = new(Guid.NewGuid());
    private static readonly VaultPath Path = VaultPath.Create("Notes/A.md").Value;
    private static readonly CommitSha Sha = CommitSha.Parse(new string('a', 40)).Value;
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    [Fact]
    public async Task Open_LoadsHistoryAndPreviewAndRestores()
    {
        var history = Substitute.For<IQueryHandler<GetFileHistory, IReadOnlyList<RemoteCommitInfo>>>();
        var version = Substitute.For<IQueryHandler<GetFileVersion, byte[]?>>();
        var restore = Substitute.For<ICommandHandler<RestoreFileVersion, Unit>>();
        var commit = new RemoteCommitInfo(Sha, "edit", "Octo", "octo@example.com", Now);
        history.HandleAsync(Arg.Any<GetFileHistory>(), Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<RemoteCommitInfo>>.Success([commit]));
        version.HandleAsync(Arg.Any<GetFileVersion>(), Arg.Any<CancellationToken>())
            .Returns(Result<byte[]?>.Success("old"u8.ToArray()));
        restore.HandleAsync(Arg.Any<RestoreFileVersion>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        var notifications = new FakeNotificationService();
        var vm = new FileHistoryViewModel(
            new ViewModelServices(new ImmediateDispatcher(), notifications, PresentationFakes.Localizer(), new FakeTimeProvider(Now)),
            new FileHistoryUseCases(history, version, restore));
        vm.Attach(Vault);

        await vm.OpenAsync(Path);
        await vm.PreviewCommand.ExecuteAsync(null);
        await vm.RestoreCommand.ExecuteAsync(null);

        vm.Commits.Should().ContainSingle();
        vm.PreviewText.Should().Be("old");
        await restore.Received(1).HandleAsync(Arg.Any<RestoreFileVersion>(), Arg.Any<CancellationToken>());
        notifications.Shown.Should().ContainSingle();
    }
}

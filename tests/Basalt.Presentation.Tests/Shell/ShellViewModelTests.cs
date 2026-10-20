using Basalt.Application.Abstractions;
using Basalt.Application.Errors;
using Basalt.Application.Vaults;
using Basalt.Domain.Common;
using Basalt.Domain.Vaults;
using Basalt.Presentation.Common;
using Basalt.Presentation.Shell;
using Basalt.Presentation.Vaults;
using Basalt.Testing.Fakes;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Basalt.Presentation.Tests.Shell;

public sealed class ShellViewModelTests
{
    private static readonly VaultId IdA = new(Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001"));
    private static readonly VaultId IdB = new(Guid.Parse("bbbbbbbb-0000-4000-8000-000000000002"));

    private sealed class FakeWorkspace(ViewModelServices services, VaultSessionInfo info) : ViewModelBase(services), IClosable
    {
        public VaultSessionInfo Info { get; } = info;

        public int Closed { get; private set; }

        public Task CloseAsync(CancellationToken ct)
        {
            Closed++;
            return Task.CompletedTask;
        }
    }

    private readonly ViewModelServices _services = new(new ImmediateDispatcher(), new FakeNotificationService(), PresentationFakes.Localizer(), new FakeTimeProvider());
    private readonly ICommandHandler<OpenVault, VaultSessionInfo> _open = Substitute.For<ICommandHandler<OpenVault, VaultSessionInfo>>();
    private readonly ICommandHandler<CloseVault, Unit> _close = Substitute.For<ICommandHandler<CloseVault, Unit>>();
    private readonly IWindowService _windows = Substitute.For<IWindowService>();
    private readonly IQueryHandler<ListVaults, IReadOnlyList<VaultListItem>> _list = Substitute.For<IQueryHandler<ListVaults, IReadOnlyList<VaultListItem>>>();

    public ShellViewModelTests()
    {
        _open.HandleAsync(Arg.Any<OpenVault>(), Arg.Any<CancellationToken>())
            .Returns(c => Result.Success(new VaultSessionInfo(c.Arg<OpenVault>().VaultId, c.Arg<OpenVault>().VaultId == IdA ? "Notas" : "Trabajo", "C:/v", VaultSettings.Default, null)));
        _close.HandleAsync(Arg.Any<CloseVault>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        _list.HandleAsync(Arg.Any<ListVaults>(), Arg.Any<CancellationToken>()).Returns(Result.Success<IReadOnlyList<VaultListItem>>([]));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ShellViewModel Shell(bool multipleWindows = true) => new(
        _services,
        new ShellOptions(multipleWindows),
        _open,
        _close,
        _windows,
        opener => new VaultPickerViewModel(_services, new VaultPickerUseCases(_list, null!, null!, null!, null!), new FakeDialogService(), opener),
        info => new FakeWorkspace(_services, info));

    [Fact]
    public async Task StartsOnThePicker()
    {
        var shell = Shell();
        await shell.InitializeAsync(null, Ct);

        shell.Content.Should().BeOfType<VaultPickerViewModel>();
        shell.Title.Should().Be("Basalt");
        shell.CloseVaultCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task OpeningAVault_ShowsItsWorkspace()
    {
        var shell = Shell();
        await shell.InitializeAsync(IdA, Ct);

        ((FakeWorkspace)shell.Content).Info.Id.Should().Be(IdA);
        shell.Title.Should().Be("Notas — Basalt");
        shell.ActiveVault.Should().Be(IdA);
    }

    [Fact]
    [Trait("Spec", "F-001.7")]
    public async Task ASecondVault_OpensInANewWindow_OnDesktop()
    {
        var shell = Shell(multipleWindows: true);
        await shell.InitializeAsync(IdA, Ct);

        (await shell.OpenAsync(IdB, Ct)).IsSuccess.Should().BeTrue();

        _windows.Received(1).OpenVaultWindow(IdB);
        shell.ActiveVault.Should().Be(IdA);
    }

    [Fact]
    public async Task ReopeningTheSameVault_StaysInThisWindow()
    {
        var shell = Shell(multipleWindows: true);
        await shell.InitializeAsync(IdA, Ct);

        await shell.OpenAsync(IdA, Ct);

        _windows.DidNotReceiveWithAnyArgs().OpenVaultWindow(default);
    }

    [Fact]
    public async Task OnIos_ASecondVault_ReplacesTheFirst()
    {
        var shell = Shell(multipleWindows: false);
        await shell.InitializeAsync(IdA, Ct);

        await shell.OpenAsync(IdB, Ct);

        await _close.Received(1).HandleAsync(new CloseVault(IdA), Arg.Any<CancellationToken>());
        shell.ActiveVault.Should().Be(IdB);
        _windows.DidNotReceiveWithAnyArgs().OpenVaultWindow(default);
    }

    [Fact]
    public async Task ClosingTheVault_ReturnsToThePicker()
    {
        var shell = Shell();
        await shell.InitializeAsync(IdA, Ct);
        var workspace = (FakeWorkspace)shell.Content;

        await shell.CloseVaultCommand.ExecuteAsync(null);

        workspace.Closed.Should().Be(1, "the workspace saves its tabs before the vault closes");
        await _close.Received(1).HandleAsync(new CloseVault(IdA), Arg.Any<CancellationToken>());
        shell.Content.Should().BeOfType<VaultPickerViewModel>();
        shell.ActiveVault.Should().BeNull();
        shell.Title.Should().Be("Basalt");
    }

    [Fact]
    public async Task OpenFailures_KeepThePicker_AndAreReturned()
    {
        _open.HandleAsync(Arg.Any<OpenVault>(), Arg.Any<CancellationToken>()).Returns(new Error(ErrorCodes.VaultLocationMissing, "x"));
        var shell = Shell();

        (await shell.OpenAsync(IdA, Ct)).Error.Code.Should().Be(ErrorCodes.VaultLocationMissing);

        shell.Content.Should().BeOfType<VaultPickerViewModel>();
    }
}

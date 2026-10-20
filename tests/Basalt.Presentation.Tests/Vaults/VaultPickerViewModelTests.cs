using Basalt.Application.Abstractions;
using Basalt.Application.Errors;
using Basalt.Application.Vaults;
using Basalt.Domain.Common;
using Basalt.Domain.Vaults;
using Basalt.Presentation.Common;
using Basalt.Presentation.Vaults;
using Basalt.Testing.Fakes;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Basalt.Presentation.Tests.Vaults;

public sealed class VaultPickerViewModelTests
{
    private static readonly VaultId IdA = new(Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001"));
    private static readonly VaultId IdB = new(Guid.Parse("bbbbbbbb-0000-4000-8000-000000000002"));
    private static readonly DateTimeOffset Added = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly IQueryHandler<ListVaults, IReadOnlyList<VaultListItem>> _list = Substitute.For<IQueryHandler<ListVaults, IReadOnlyList<VaultListItem>>>();
    private readonly ICommandHandler<CreateVault, VaultRegistration> _create = Substitute.For<ICommandHandler<CreateVault, VaultRegistration>>();
    private readonly ICommandHandler<OpenFolderAsVault, VaultRegistration> _openFolder = Substitute.For<ICommandHandler<OpenFolderAsVault, VaultRegistration>>();
    private readonly ICommandHandler<RemoveVaultFromList, Unit> _remove = Substitute.For<ICommandHandler<RemoveVaultFromList, Unit>>();
    private readonly ICommandHandler<RelocateVault, VaultRegistration> _relocate = Substitute.For<ICommandHandler<RelocateVault, VaultRegistration>>();
    private readonly IVaultOpener _opener = Substitute.For<IVaultOpener>();
    private readonly FakeDialogService _dialogs = new();
    private readonly FakeNotificationService _notifications = new();
    private readonly VaultPickerViewModel _vm;

    public VaultPickerViewModelTests()
    {
        _list.HandleAsync(Arg.Any<ListVaults>(), Arg.Any<CancellationToken>()).Returns(Result.Success<IReadOnlyList<VaultListItem>>(
        [
            new VaultListItem(new VaultRegistration(IdA, "Notas", "C:/v/Notas", Added, Added.AddDays(2)), IsOpen: true),
            new VaultListItem(new VaultRegistration(IdB, "Trabajo", "C:/v/Trabajo", Added), IsOpen: false),
        ]));
        _opener.OpenAsync(Arg.Any<VaultId>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        _vm = new VaultPickerViewModel(
            new ViewModelServices(new ImmediateDispatcher(), _notifications, PresentationFakes.Localizer(), new FakeTimeProvider()),
            new VaultPickerUseCases(_list, _create, _openFolder, _remove, _relocate),
            _dialogs,
            _opener);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static VaultRegistration Registration(VaultId id, string name) => new(id, name, "C:/v/" + name, Added);

    [Fact]
    public async Task Load_ListsVaults()
    {
        await _vm.LoadCommand.ExecuteAsync(null);

        _vm.Vaults.Select(v => (v.Name, v.Location, v.IsOpen)).Should().Equal(("Notas", "C:/v/Notas", true), ("Trabajo", "C:/v/Trabajo", false));
        _vm.IsEmpty.Should().BeFalse();
    }

    [Fact]
    [Trait("Spec", "F-001.1")]
    public async Task Create_AsksNameAndLocation_ThenOpensTheNewVault()
    {
        _dialogs.PromptAnswer = "Nuevo";
        _dialogs.FolderAnswer = "C:/v";
        _create.HandleAsync(new CreateVault("Nuevo", "C:/v"), Arg.Any<CancellationToken>()).Returns(Registration(IdB, "Nuevo"));

        await _vm.CreateVaultCommand.ExecuteAsync(null);

        await _opener.Received(1).OpenAsync(IdB, Arg.Any<CancellationToken>());
        _notifications.Shown.Should().BeEmpty();
    }

    [Fact]
    [Trait("Spec", "F-001.2")]
    public async Task Create_WithAnInvalidName_ShowsTheErrorAndOpensNothing()
    {
        _dialogs.PromptAnswer = "a/b";
        _dialogs.FolderAnswer = "C:/v";
        _create.HandleAsync(Arg.Any<CreateVault>(), Arg.Any<CancellationToken>()).Returns(new Error(DomainErrors.VaultInvalidName, "x"));

        await _vm.CreateVaultCommand.ExecuteAsync(null);

        _notifications.Shown.Single().Message.Should().Be("The vault name is not valid.");
        await _opener.DidNotReceiveWithAnyArgs().OpenAsync(default, Ct);
    }

    [Fact]
    public async Task Create_CancelledAtAnyStep_DoesNothing()
    {
        _dialogs.PromptAnswer = null;
        await _vm.CreateVaultCommand.ExecuteAsync(null);
        _dialogs.PromptAnswer = "x";
        _dialogs.FolderAnswer = null;
        await _vm.CreateVaultCommand.ExecuteAsync(null);

        await _create.DidNotReceiveWithAnyArgs().HandleAsync(default!, Ct);
    }

    [Fact]
    [Trait("Spec", "F-001.3")]
    public async Task OpenFolder_RegistersAndOpens()
    {
        _dialogs.FolderAnswer = "C:/carpeta";
        _openFolder.HandleAsync(new OpenFolderAsVault("C:/carpeta"), Arg.Any<CancellationToken>()).Returns(Registration(IdB, "carpeta"));

        await _vm.OpenFolderCommand.ExecuteAsync(null);

        await _opener.Received(1).OpenAsync(IdB, Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait("Spec", "F-001.5")]
    public async Task Remove_AsksForConfirmation_AndKeepsFiles()
    {
        await _vm.LoadCommand.ExecuteAsync(null);
        _remove.HandleAsync(Arg.Any<RemoveVaultFromList>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        _dialogs.ConfirmAnswer = false;

        await _vm.RemoveCommand.ExecuteAsync(_vm.Vaults[1]);
        await _remove.DidNotReceiveWithAnyArgs().HandleAsync(default!, Ct);

        _dialogs.ConfirmAnswer = true;
        await _vm.RemoveCommand.ExecuteAsync(_vm.Vaults[1]);

        await _remove.Received(1).HandleAsync(new RemoveVaultFromList(IdB), Arg.Any<CancellationToken>());
        _dialogs.Requests.OfType<ConfirmRequest>().Last().Message.Should().Be("“Trabajo” will be removed from the list. Its files are not deleted.");
    }

    [Fact]
    [Trait("Spec", "F-001.6")]
    public async Task OpenMissingVault_OffersLocate_ThenRetries()
    {
        await _vm.LoadCommand.ExecuteAsync(null);
        _opener.OpenAsync(IdB, Arg.Any<CancellationToken>()).Returns(new Error(ErrorCodes.VaultLocationMissing, "x"), Result.Success());
        _dialogs.ChoiceAnswer = 0;
        _dialogs.FolderAnswer = "D:/movido";
        _relocate.HandleAsync(new RelocateVault(IdB, "D:/movido"), Arg.Any<CancellationToken>()).Returns(Registration(IdB, "Trabajo"));

        await _vm.OpenCommand.ExecuteAsync(_vm.Vaults[1]);

        var choice = _dialogs.Requests.OfType<ChoiceRequest>().Single();
        choice.Options.Should().Equal("Locate…", "Remove from list");
        await _opener.Received(2).OpenAsync(IdB, Arg.Any<CancellationToken>());
        _notifications.Shown.Should().BeEmpty();
    }

    [Fact]
    [Trait("Spec", "F-001.6")]
    public async Task OpenMissingVault_CanRemoveIt()
    {
        await _vm.LoadCommand.ExecuteAsync(null);
        _opener.OpenAsync(IdB, Arg.Any<CancellationToken>()).Returns(new Error(ErrorCodes.VaultLocationMissing, "x"));
        _remove.HandleAsync(Arg.Any<RemoveVaultFromList>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        _dialogs.ChoiceAnswer = 1;

        await _vm.OpenCommand.ExecuteAsync(_vm.Vaults[1]);

        await _remove.Received(1).HandleAsync(new RemoveVaultFromList(IdB), Arg.Any<CancellationToken>());
        _dialogs.Requests.OfType<ConfirmRequest>().Should().BeEmpty();
    }

    [Fact]
    public async Task OpenErrors_OtherThanMissing_AreNotified()
    {
        await _vm.LoadCommand.ExecuteAsync(null);
        _opener.OpenAsync(IdB, Arg.Any<CancellationToken>()).Returns(new Error(ErrorCodes.VaultConfigInvalid, "x"));

        await _vm.OpenCommand.ExecuteAsync(_vm.Vaults[1]);

        _notifications.Shown.Single().Message.Should().Be("The vault settings file is not valid.");
        _dialogs.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task EmptyList_IsReported()
    {
        _list.HandleAsync(Arg.Any<ListVaults>(), Arg.Any<CancellationToken>()).Returns(Result.Success<IReadOnlyList<VaultListItem>>([]));

        await _vm.LoadCommand.ExecuteAsync(null);

        _vm.IsEmpty.Should().BeTrue();
    }
}

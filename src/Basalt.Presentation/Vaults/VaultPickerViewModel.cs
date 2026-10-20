using System.Collections.ObjectModel;
using Basalt.Application.Abstractions;
using Basalt.Application.Errors;
using Basalt.Application.Vaults;
using Basalt.Domain.Common;
using Basalt.Domain.Vaults;
using Basalt.Presentation.Common;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Basalt.Presentation.Vaults;

/// <summary>Opens a vault where it belongs (this window, a new window…); implemented by the shell.</summary>
public interface IVaultOpener
{
    /// <returns>The <c>OpenVault</c> errors, e.g. <c>vault.location_missing</c>.</returns>
    Task<Result<Unit>> OpenAsync(VaultId vault, CancellationToken ct);
}

/// <summary>The use cases behind the vault picker.</summary>
public sealed record VaultPickerUseCases(
    IQueryHandler<ListVaults, IReadOnlyList<VaultListItem>> List,
    ICommandHandler<CreateVault, VaultRegistration> Create,
    ICommandHandler<OpenFolderAsVault, VaultRegistration> OpenFolder,
    ICommandHandler<RemoveVaultFromList, Unit> Remove,
    ICommandHandler<RelocateVault, VaultRegistration> Relocate);

/// <summary>A registered vault in the picker.</summary>
public sealed class VaultItemViewModel(VaultListItem item)
{
    public VaultId Id { get; } = item.Registration.Id;

    public string Name { get; } = item.Registration.Name;

    public string Location { get; } = item.Registration.RootLocation;

    public bool IsOpen { get; } = item.IsOpen;

    public DateTimeOffset? LastOpenedAt { get; } = item.Registration.LastOpenedAt;
}

/// <summary>
/// The start screen (F-001): registered vaults, create, open a folder as vault, remove from the list (files are kept)
/// and, when a vault's folder is gone, locate it or remove it (F-001.6).
/// </summary>
public sealed partial class VaultPickerViewModel(ViewModelServices services, VaultPickerUseCases useCases, IDialogService dialogs, IVaultOpener opener)
    : ViewModelBase(services)
{
    public ObservableCollection<VaultItemViewModel> Vaults { get; } = [];

    [ObservableProperty]
    public partial bool IsEmpty { get; private set; }

    [RelayCommand]
    private async Task LoadAsync()
    {
        var vaults = await RunAsync(ct => useCases.List.HandleAsync(new ListVaults(), ct)).ConfigureAwait(true);
        if (vaults.IsFailure)
        {
            return;
        }

        Vaults.Clear();
        foreach (var vault in vaults.Value)
        {
            Vaults.Add(new VaultItemViewModel(vault));
        }

        IsEmpty = Vaults.Count == 0;
    }

    [RelayCommand]
    private async Task CreateVaultAsync(CancellationToken ct)
    {
        var name = await dialogs.PromptAsync(new PromptRequest(Localizer.Get("VaultPicker.Create"), Localizer.Get("VaultPicker.NameLabel")), ct).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var parent = await dialogs.PickFolderAsync(Localizer.Get("VaultPicker.ChooseLocation"), ct).ConfigureAwait(true);
        if (parent is null)
        {
            return;
        }

        var created = await RunAsync(token => useCases.Create.HandleAsync(new CreateVault(name.Trim(), parent), token)).ConfigureAwait(true);
        if (created.IsSuccess)
        {
            await OpenVaultAsync(created.Value, ct).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task OpenFolderAsync(CancellationToken ct)
    {
        var folder = await dialogs.PickFolderAsync(Localizer.Get("VaultPicker.OpenFolder"), ct).ConfigureAwait(true);
        if (folder is null)
        {
            return;
        }

        var registered = await RunAsync(token => useCases.OpenFolder.HandleAsync(new OpenFolderAsVault(folder), token)).ConfigureAwait(true);
        if (registered.IsSuccess)
        {
            await OpenVaultAsync(registered.Value, ct).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private Task OpenAsync(VaultItemViewModel item, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(item);
        return OpenVaultAsync(new VaultRegistration(item.Id, item.Name, item.Location, default), ct);
    }

    [RelayCommand]
    private async Task RemoveAsync(VaultItemViewModel item, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(item);
        var remove = Localizer.Get("VaultPicker.Remove");
        if (await dialogs.ConfirmAsync(new ConfirmRequest(remove, Localizer.Format("VaultPicker.RemoveMessage", item.Name), remove), ct).ConfigureAwait(true))
        {
            await RemoveVaultAsync(item.Id).ConfigureAwait(true);
        }
    }

    private async Task OpenVaultAsync(VaultRegistration vault, CancellationToken ct)
    {
        var opened = await RunAsync(token => opener.OpenAsync(vault.Id, token), notifyErrors: false).ConfigureAwait(true);
        if (opened.IsSuccess || opened.Error.Code == ErrorCodes.OperationCancelled)
        {
            return;
        }

        if (opened.Error.Code != ErrorCodes.VaultLocationMissing)
        {
            Services.Notifications.Show(new Notification(NotificationSeverity.Error, Localizer.Error(opened.Error)));
            return;
        }

        var choice = await dialogs.ChooseAsync(
            new ChoiceRequest(
                Localizer.Get("VaultPicker.MissingTitle"),
                Localizer.Format("VaultPicker.MissingMessage", vault.Name, vault.RootLocation),
                [Localizer.Get("VaultPicker.Locate"), Localizer.Get("VaultPicker.Remove")]),
            ct).ConfigureAwait(true);
        switch (choice)
        {
            case 0:
                var folder = await dialogs.PickFolderAsync(Localizer.Format("VaultPicker.LocateTitle", vault.Name), ct).ConfigureAwait(true);
                if (folder is not null)
                {
                    var relocated = await RunAsync(token => useCases.Relocate.HandleAsync(new RelocateVault(vault.Id, folder), token)).ConfigureAwait(true);
                    if (relocated.IsSuccess)
                    {
                        await OpenVaultAsync(relocated.Value, ct).ConfigureAwait(true);
                    }
                }

                break;
            case 1:
                await RemoveVaultAsync(vault.Id).ConfigureAwait(true);
                break;
        }
    }

    private async Task RemoveVaultAsync(VaultId id)
    {
        var removed = await RunAsync(token => useCases.Remove.HandleAsync(new RemoveVaultFromList(id), token)).ConfigureAwait(true);
        if (removed.IsSuccess)
        {
            await LoadAsync().ConfigureAwait(true);
        }
    }
}

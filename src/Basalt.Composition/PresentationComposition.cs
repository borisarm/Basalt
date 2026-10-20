using System.Globalization;
using Basalt.Application.Abstractions;
using Basalt.Application.Bookmarks;
using Basalt.Application.Commands;
using Basalt.Application.Graph;
using Basalt.Application.Links;
using Basalt.Application.Notes;
using Basalt.Application.Ports.Events;
using Basalt.Application.Ports.Index;
using Basalt.Application.Ports.Platform;
using Basalt.Application.Search;
using Basalt.Application.Tags;
using Basalt.Application.Vaults;
using Basalt.Application.Workspace;
using Basalt.Domain.Common;
using Basalt.Domain.Graph;
using Basalt.Domain.Notes;
using Basalt.Domain.Tags;
using Basalt.Presentation.Common;
using Basalt.Presentation.Editor;
using Basalt.Presentation.Input;
using Basalt.Presentation.Localization;
using Basalt.Presentation.Overlays;
using Basalt.Presentation.Panels;
using Basalt.Presentation.Settings;
using Basalt.Presentation.Shell;
using Basalt.Presentation.Vaults;
using Basalt.Presentation.Workspace;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Basalt.Composition;

/// <summary>
/// DI registration of Presentation (spec 07 §2). The UI layer must register <see cref="IUiDispatcher"/>,
/// <see cref="INotificationService"/>, <see cref="IDialogService"/>, <see cref="IWindowService"/> and
/// <see cref="IThemeService"/>.
/// </summary>
public static class PresentationComposition
{
    public static IServiceCollection AddBasaltPresentation(this IServiceCollection services, CommandPlatform platform, ShellOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<ILocalizer>(_ => new ResxLocalizer(CultureInfo.CurrentUICulture));
        services.TryAddSingleton<IEditorCommands, EditorCommandsService>();
        services.TryAddSingleton(sp => new KeyBindingService(sp.GetRequiredService<ICommandRegistry>(), platform));
        services.TryAddSingleton(options);
        services.TryAddSingleton(sp => new ViewModelServices(
            sp.GetRequiredService<IUiDispatcher>(),
            sp.GetRequiredService<INotificationService>(),
            sp.GetRequiredService<ILocalizer>(),
            sp.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<ShellFactory>();
        services.TryAddSingleton<WorkspaceFactory>();
        services.TryAddTransient(sp => new SettingsDependencies(
            sp.GetRequiredService<Application.Settings.IAppSettingsStore>(),
            sp.GetRequiredService<ICommandHandler<UpdateVaultSettings, Unit>>(),
            sp.GetRequiredService<IThemeService>(),
            sp.GetRequiredService<IPlatformPaths>(),
            sp.GetRequiredService<KeyBindingService>(),
            sp.GetRequiredService<ICommandRegistry>()));
        return services;
    }
}

/// <summary>Creates the root ViewModel of a window (desktop) or of the app (iOS).</summary>
public sealed class ShellFactory(IServiceProvider services)
{
    public ShellViewModel Create()
    {
        var common = services.GetRequiredService<ViewModelServices>();
        var workspaces = services.GetRequiredService<WorkspaceFactory>();
        var shell = new ShellViewModel(
            common,
            services.GetRequiredService<ShellOptions>(),
            services.GetRequiredService<ICommandHandler<OpenVault, VaultSessionInfo>>(),
            services.GetRequiredService<ICommandHandler<CloseVault, Unit>>(),
            services.GetRequiredService<IWindowService>(),
            opener => new VaultPickerViewModel(
                common,
                new VaultPickerUseCases(
                    services.GetRequiredService<IQueryHandler<ListVaults, IReadOnlyList<VaultListItem>>>(),
                    services.GetRequiredService<ICommandHandler<CreateVault, VaultRegistration>>(),
                    services.GetRequiredService<ICommandHandler<OpenFolderAsVault, VaultRegistration>>(),
                    services.GetRequiredService<ICommandHandler<RemoveVaultFromList, Unit>>(),
                    services.GetRequiredService<ICommandHandler<RelocateVault, VaultRegistration>>()),
                services.GetRequiredService<IDialogService>(),
                opener),
            workspaces.Create);

        // F-014 "open last vault": remember the vault opened most recently.
        var settings = services.GetRequiredService<Application.Settings.IAppSettingsStore>();
        shell.VaultOpened += (_, vault) => _ = RememberAsync(settings, vault);
        return shell;
    }

    private static async Task RememberAsync(Application.Settings.IAppSettingsStore settings, Domain.Vaults.VaultId vault)
    {
        var current = await settings.GetAsync(CancellationToken.None).ConfigureAwait(false);
        await settings.SaveAsync(current with { LastVaultId = vault }, CancellationToken.None).ConfigureAwait(false);
    }

    public SettingsViewModel CreateSettings(VaultSessionInfo? vault) =>
        new(services.GetRequiredService<ViewModelServices>(), services.GetRequiredService<SettingsDependencies>(), vault);
}

/// <summary>Builds the workspace of an open vault with its tabs, panels and overlays.</summary>
public sealed class WorkspaceFactory(IServiceProvider services)
{
    private T Get<T>()
        where T : notnull => services.GetRequiredService<T>();

    public WorkspaceViewModel Create(VaultSessionInfo vault)
    {
        ArgumentNullException.ThrowIfNull(vault);
        var common = Get<ViewModelServices>();
        var events = Get<IEventBus>();
        var messenger = new WorkspaceMessenger();
        var noteTabs = new NoteTabUseCases(Get<IQueryHandler<ReadNote, NoteDocument>>(), Get<ICommandHandler<SaveNote, Domain.Sync.BlobSha>>());
        var editing = new NoteEditingUseCases(
            Get<IQueryHandler<SuggestLinkTargets, IReadOnlyList<LinkSuggestion>>>(),
            Get<IQueryHandler<SuggestTags, IReadOnlyList<TagSuggestion>>>(),
            Get<ICommandHandler<EnsureBlockId, BlockId>>(),
            Get<ICommandHandler<ImportAttachment, ImportedAttachment>>());
        var workspace = new WorkspaceViewModel(
            common,
            vault,
            [new DelegateTabFactory("note", p => FileClassifier.Classify(p) == FileKind.Note, () => new NoteTabViewModel(common, noteTabs, events, messenger, vault.Id, editing, Get<IEditorCommands>()))],
            messenger,
            new WorkspaceUseCases(Get<ICommandHandler<RecordRecentFile, Unit>>(), Get<IQueryHandler<LoadWorkspace, WorkspaceState?>>(), Get<ICommandHandler<SaveWorkspace, Unit>>()));

        var context = new PanelContext(vault.Id, events, messenger, workspace);
        workspace.Panels = new WorkspacePanels(
            new FileExplorerViewModel(common, context, new FileExplorerUseCases(
                Get<IQueryHandler<GetFileTree, FileTreeNode>>(),
                Get<ICommandHandler<CreateNote, VaultPath>>(),
                Get<ICommandHandler<CreateFolder, VaultPath>>(),
                Get<ICommandHandler<RenameEntry, VaultPath>>(),
                Get<ICommandHandler<DeleteEntries, Unit>>()), Get<IDialogService>()),
            new SearchPanelViewModel(common, context, Get<IQueryHandler<SearchNotes, IReadOnlyList<SearchHit>>>()),
            new BookmarksViewModel(common, context, Get<IQueryHandler<ListBookmarks, BookmarksView>>(), Get<ICommandHandler<RemoveBookmark, Unit>>()),
            new TagsPanelViewModel(common, context, Get<IQueryHandler<GetTags, TagHierarchy>>()),
            new BacklinksViewModel(common, context, Get<IQueryHandler<GetBacklinks, Backlinks>>(), Get<ICommandHandler<LinkUnlinkedMention, Unit>>()),
            new OutgoingLinksViewModel(common, context, Get<IQueryHandler<GetOutgoingLinks, IReadOnlyList<OutgoingLink>>>()),
            new OutlineViewModel(common, context, Get<IQueryHandler<GetOutline, IReadOnlyList<OutlineNode>>>()),
            new PropertiesViewModel(common, context, new PropertiesUseCases(
                Get<IQueryHandler<ReadNote, NoteDocument>>(),
                Get<ICommandHandler<SetFrontmatterProperty, Unit>>(),
                Get<ICommandHandler<RemoveFrontmatterProperty, Unit>>())),
            new LocalGraphViewModel(common, context, Get<IQueryHandler<GetLocalGraph, GraphModel>>(), Get<IRandom>()),
            new QuickSwitcherViewModel(common, vault.Id, Get<IQueryHandler<QuickSwitch, IReadOnlyList<QuickSwitchItem>>>(), Get<ICommandHandler<CreateNote, VaultPath>>(), workspace),
            new CommandPaletteViewModel(common, Get<ICommandRegistry>(), Get<KeyBindingService>(), () => new CommandContext(vault.Id, workspace.ActiveFile, workspace)));
        return workspace;
    }
}

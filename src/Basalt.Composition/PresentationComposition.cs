using System.Globalization;
using Basalt.Application.Abstractions;
using Basalt.Application.Accounts;
using Basalt.Application.Bookmarks;
using Basalt.Application.Commands;
using Basalt.Application.Graph;
using Basalt.Application.Links;
using Basalt.Application.Notes;
using Basalt.Application.Ports.Auth;
using Basalt.Application.Ports.Events;
using Basalt.Application.Ports.Index;
using Basalt.Application.Ports.Platform;
using Basalt.Application.Ports.Remote;
using Basalt.Application.Ports.Storage;
using Basalt.Application.Search;
using Basalt.Application.Sync;
using Basalt.Application.Tags;
using Basalt.Application.Vaults;
using Basalt.Application.Workspace;
using Basalt.Domain.Accounts;
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
using Basalt.Presentation.Sync;
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
        services.TryAddEnumerable(ServiceDescriptor.Transient<ICommandContribution, WorkspaceCommands>());
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

    public GitHubSignInViewModel CreateSignIn() =>
        new(services.GetRequiredService<ViewModelServices>(), new GitHubSignInUseCases(
            services.GetRequiredService<ICommandHandler<StartGitHubSignIn, DeviceCodeChallenge>>(),
            services.GetRequiredService<ICommandHandler<CompleteGitHubSignIn, GitHubAccount>>()));

    public ConfigureSyncWizardViewModel CreateConfigureSync(VaultSessionInfo vault)
    {
        ArgumentNullException.ThrowIfNull(vault);
        var viewModel = new ConfigureSyncWizardViewModel(
            services.GetRequiredService<ViewModelServices>(),
            new ConfigureSyncUseCases(
                services.GetRequiredService<IQueryHandler<PreviewSyncLink, SyncLinkPreview>>(),
                services.GetRequiredService<ICommandHandler<ConfigureSync, SyncLinkPreview>>(),
                services.GetRequiredService<IQueryHandler<ListAccounts, IReadOnlyList<GitHubAccount>>>(),
                services.GetRequiredService<ICommandHandler<CreateRepositoryForVault, RemoteRepositoryInfo>>()));
        viewModel.Attach(vault.Id, vault.Name);
        return viewModel;
    }

    public SettingsViewModel CreateSettings(VaultSessionInfo? vault)
    {
        var common = services.GetRequiredService<ViewModelServices>();
        SyncSettingsViewModels? sync = null;
        if (vault is not null)
        {
            var accounts = new AccountsSettingsViewModel(common, new AccountsSettingsUseCases(
                services.GetRequiredService<IQueryHandler<ListAccounts, IReadOnlyList<GitHubAccount>>>(),
                services.GetRequiredService<ICommandHandler<RemoveAccount, Unit>>()));
            var vaultSync = new VaultSyncSettingsViewModel(common, services.GetRequiredService<ISyncProfileStore>(), new VaultSyncSettingsUseCases(
                services.GetRequiredService<ICommandHandler<PauseSync, Unit>>(),
                services.GetRequiredService<ICommandHandler<ResumeSync, Unit>>(),
                services.GetRequiredService<ICommandHandler<DisableSync, Unit>>()));
            sync = new SyncSettingsViewModels(accounts, vaultSync);
        }

        return new SettingsViewModel(common, services.GetRequiredService<SettingsDependencies>(), vault, sync,
            services.GetRequiredService<IWindowService>());
    }
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
        var reading = new NoteReadingUseCases(
            Get<IQueryHandler<RenderNote, Application.Ports.Markdown.RenderDocument>>(),
            Get<IQueryHandler<ResolveLink, VaultPath?>>(),
            Get<IQueryHandler<GetEmbed, EmbedContent>>(),
            Get<IQueryHandler<ReadAttachment, byte[]>>(),
            Get<ICommandHandler<ToggleTask, Unit>>(),
            Get<ICommandHandler<CreateNote, VaultPath>>());
        WorkspaceViewModel? workspace = null;
        workspace = new WorkspaceViewModel(
            common,
            vault,
            [
                new DelegateTabFactory("note", p => FileClassifier.Classify(p) == FileKind.Note, () => new NoteTabViewModel(common, noteTabs, events, messenger, vault.Id, editing, Get<IEditorCommands>(), reading)),
                new DelegateTabFactory(GraphTabViewModel.KindId, _ => false,
                    () => new GraphTabViewModel(common, vault.Id, Get<IQueryHandler<GetGraph, GraphModel>>(), workspace!, Get<IRandom>(), events)),
                new DelegateTabFactory(ConflictCenterViewModel.KindId, _ => false,
                    () =>
                    {
                        var tab = new ConflictCenterViewModel(common, new ConflictCenterUseCases(
                            Get<IQueryHandler<ListConflicts, IReadOnlyList<SyncConflictEntry>>>(),
                            Get<ICommandHandler<ResolveConflict, Unit>>()));
                        tab.Attach(vault.Id);
                        return tab;
                    }),
                new DelegateTabFactory(FileHistoryViewModel.KindId, path => FileClassifier.Classify(path) == FileKind.Note,
                    () =>
                    {
                        var tab = new FileHistoryViewModel(common, new FileHistoryUseCases(
                            Get<IQueryHandler<GetFileHistory, IReadOnlyList<RemoteCommitInfo>>>(),
                            Get<IQueryHandler<GetFileVersion, byte[]?>>(),
                            Get<ICommandHandler<RestoreFileVersion, Unit>>(),
                            Get<IQueryHandler<ReadNote, NoteDocument>>()));
                        tab.Attach(vault.Id);
                        return tab;
                    }),
            ],
            messenger,
            new WorkspaceUseCases(Get<ICommandHandler<RecordRecentFile, Unit>>(), Get<IQueryHandler<LoadWorkspace, WorkspaceState?>>(), Get<ICommandHandler<SaveWorkspace, Unit>>()),
            Get<IWindowService>());

        workspace.Sync = new SyncStatusViewModel(common, new SyncStatusUseCases(
            Get<IQueryHandler<GetSyncStatus, SyncStatusView>>(),
            Get<ICommandHandler<SyncVault, SyncCycleResult?>>(),
            Get<ICommandHandler<PauseSync, Unit>>(),
            Get<ICommandHandler<ResumeSync, Unit>>()));
        workspace.Sync.Attach(vault.Id);

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

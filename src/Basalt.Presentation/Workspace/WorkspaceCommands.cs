using Basalt.Application.Commands;
using Basalt.Domain.Common;
using Basalt.Presentation.Panels;

namespace Basalt.Presentation.Workspace;

/// <summary>What the workspace commands ask of the open workspace (implemented by <see cref="WorkspaceViewModel"/>).</summary>
public interface IWorkspaceCommandHost : ICommandHost
{
    Task ShowQuickSwitcherAsync();

    void ShowCommandPalette();

    void ShowPanel(string panelId);

    Task ToggleReadingModeAsync();

    Task CloseActiveTabAsync(CancellationToken ct);

    Task ReopenClosedTabAsync(CancellationToken ct);

    Task GoBackAsync(CancellationToken ct);

    Task GoForwardAsync(CancellationToken ct);

    Task OpenGraphAsync(CancellationToken ct);

    Task OpenSettingsAsync();
}

/// <summary>Commands that act on views (spec 07 §9), with the default shortcuts of spec 04.</summary>
public sealed class WorkspaceCommands : ICommandContribution
{
    public const string QuickSwitcher = "workspace.quick-switcher";
    public const string CommandPalette = "workspace.command-palette";
    public const string SearchVault = "workspace.search";
    public const string ToggleReadingMode = "editor.toggle-reading-mode";
    public const string CloseTab = "workspace.close-tab";
    public const string ReopenClosedTab = "workspace.reopen-closed-tab";
    public const string GoBack = "workspace.go-back";
    public const string GoForward = "workspace.go-forward";
    public const string OpenGraph = "workspace.open-graph";
    public const string OpenSettings = "workspace.open-settings";

    public IEnumerable<CommandDescriptor> GetCommands() =>
    [
        Command(QuickSwitcher, "Menu.QuickSwitcher", "Mod+O", (host, _) => host.ShowQuickSwitcherAsync()),
        Command(CommandPalette, "Menu.CommandPalette", "Mod+P", (host, _) => Run(host.ShowCommandPalette)),
        Command(SearchVault, "Commands.SearchVault", "Mod+Shift+F", (host, _) => Run(() => host.ShowPanel(PanelIds.Search))),
        Command(ToggleReadingMode, "Commands.ToggleReadingMode", "Mod+E", (host, _) => host.ToggleReadingModeAsync()),
        Command(CloseTab, "Workspace.CloseTab", "Mod+W", (host, ct) => host.CloseActiveTabAsync(ct)),
        Command(ReopenClosedTab, "Commands.ReopenClosedTab", "Mod+Shift+T", (host, ct) => host.ReopenClosedTabAsync(ct)),
        Command(GoBack, "Commands.GoBack", "Alt+Left;Mod+[", (host, ct) => host.GoBackAsync(ct)),
        Command(GoForward, "Commands.GoForward", "Alt+Right;Mod+]", (host, ct) => host.GoForwardAsync(ct)),
        Command(OpenGraph, "Commands.OpenGraph", null, (host, ct) => host.OpenGraphAsync(ct)),
        Command(OpenSettings, "Commands.OpenSettings", "Mod+,", (host, _) => host.OpenSettingsAsync()),
    ];

    private static CommandDescriptor Command(string id, string titleKey, string? gesture, Func<IWorkspaceCommandHost, CancellationToken, Task> run) =>
        new(id, titleKey, "workspace", gesture, c => c.Host is IWorkspaceCommandHost, async (context, ct) =>
        {
            await run((IWorkspaceCommandHost)context.Host, ct).ConfigureAwait(true);
            return Result.Success();
        });

    private static Task Run(Action action)
    {
        action();
        return Task.CompletedTask;
    }
}

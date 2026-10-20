using Basalt.Application.Commands;
using Basalt.Domain.Vaults;
using Basalt.Presentation.Input;
using Basalt.Presentation.Workspace;
using NSubstitute;

namespace Basalt.Presentation.Tests.Workspace;

public sealed class WorkspaceCommandsTests
{
    private static readonly VaultId Vault = new(Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Gesture G(string text, CommandPlatform platform = CommandPlatform.Windows) => Gesture.Parse(text, platform).Value;

    public static TheoryData<string, string, string> Defaults => new()
    {
        { WorkspaceCommands.QuickSwitcher, "Ctrl+O", "Cmd+O" },
        { WorkspaceCommands.CommandPalette, "Ctrl+P", "Cmd+P" },
        { WorkspaceCommands.SearchVault, "Ctrl+Shift+F", "Cmd+Shift+F" },
        { WorkspaceCommands.ToggleReadingMode, "Ctrl+E", "Cmd+E" },
        { WorkspaceCommands.CloseTab, "Ctrl+W", "Cmd+W" },
        { WorkspaceCommands.ReopenClosedTab, "Ctrl+Shift+T", "Cmd+Shift+T" },
        { WorkspaceCommands.GoBack, "Alt+Left", "Cmd+[" },
        { WorkspaceCommands.GoForward, "Alt+Right", "Cmd+]" },
    };

    [Theory]
    [MemberData(nameof(Defaults))]
    [Trait("Spec", "F-008")]
    public void DefaultGestures_FollowTheShortcutTable(string id, string windows, string mac)
    {
        var registry = new CommandRegistry([new WorkspaceCommands()]);

        new KeyBindingService(registry, CommandPlatform.Windows).GestureFor(id)!.ToString(CommandPlatform.Windows).Should().Be(windows);
        new KeyBindingService(registry, CommandPlatform.MacOS).GestureFor(id)!.ToString(CommandPlatform.MacOS).Should().Be(mac);
        registry.FindByGesture(G(mac, CommandPlatform.MacOS), CommandPlatform.MacOS)!.Id.Should().Be(id);
    }

    [Fact]
    public void Gesture_ParseDefault_PicksThePlatformVariant()
    {
        Gesture.ParseDefault("Alt+Left;Mod+[", CommandPlatform.Windows).Value.Should().Be(G("Alt+Left"));
        Gesture.ParseDefault("Alt+Left;Mod+[", CommandPlatform.MacOS).Value.Should().Be(new Gesture(GestureModifiers.Meta, "["));
        Gesture.ParseDefault("Mod+N", CommandPlatform.MacOS).Value.Should().Be(new Gesture(GestureModifiers.Meta, "N"));
        Gesture.ParseDefault("a;b;c", CommandPlatform.Windows).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void UserGestures_MayUseTheSemicolonKey()
    {
        var bindings = new KeyBindingService(new CommandRegistry([new WorkspaceCommands()]), CommandPlatform.Windows);
        bindings.ApplyOverrides(new Dictionary<string, string> { [WorkspaceCommands.GoBack] = "Ctrl+;" });

        bindings.GestureFor(WorkspaceCommands.GoBack).Should().Be(G("Ctrl+;"));
        bindings.Resolve(G("Ctrl+;"))!.Id.Should().Be(WorkspaceCommands.GoBack);
    }

    [Fact]
    [Trait("Spec", "F-008")]
    public async Task Commands_RunOnTheWorkspaceHost()
    {
        var registry = new CommandRegistry([new WorkspaceCommands()]);
        var host = Substitute.For<IWorkspaceCommandHost>();
        var context = new CommandContext(Vault, null, host);

        foreach (var id in registry.Commands.Select(c => c.Id))
        {
            (await registry.ExecuteAsync(id, context, Ct)).IsSuccess.Should().BeTrue(id);
        }

        await host.Received(1).ShowQuickSwitcherAsync();
        host.Received(1).ShowCommandPalette();
        host.Received(1).ShowPanel(Presentation.Panels.PanelIds.Search);
        await host.Received(1).ToggleReadingModeAsync();
        await host.Received(1).CloseActiveTabAsync(Arg.Any<CancellationToken>());
        await host.Received(1).ReopenClosedTabAsync(Arg.Any<CancellationToken>());
        await host.Received(1).GoBackAsync(Arg.Any<CancellationToken>());
        await host.Received(1).GoForwardAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Commands_NeedAWorkspace()
    {
        var registry = new CommandRegistry([new WorkspaceCommands()]);
        var plain = new CommandContext(Vault, null, Substitute.For<ICommandHost>());

        registry.Commands.Should().OnlyContain(c => !c.CanExecute(plain));
    }

    [Fact]
    public void DailyNote_HasItsShortcut()
    {
        var core = new CoreCommands(null!, null!, null!);

        core.GetCommands().Single(c => c.Id == CoreCommands.OpenDailyNote).DefaultGesture.Should().Be("Mod+Shift+D");
    }
}

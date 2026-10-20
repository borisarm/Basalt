using Basalt.Application.Abstractions;
using Basalt.Application.Commands;
using Basalt.Application.Errors;
using Basalt.Application.Notes;
using Basalt.Application.Search;
using Basalt.Domain.Common;
using Basalt.Domain.Notes;
using Basalt.Domain.Vaults;
using Basalt.Presentation.Common;
using Basalt.Presentation.Input;
using Basalt.Presentation.Overlays;
using Basalt.Testing.Fakes;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Basalt.Presentation.Tests.Overlays;

public sealed class OverlaysTests
{
    private static readonly VaultId Vault = new(Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001"));

    private readonly FakeNotificationService _notifications = new();
    private readonly IWorkspaceNavigator _navigator = Substitute.For<IWorkspaceNavigator>();
    private readonly ICommandHost _host = Substitute.For<ICommandHost>();

    private ViewModelServices Services => new(new ImmediateDispatcher(), _notifications, PresentationFakes.Localizer(), new FakeTimeProvider());

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static VaultPath P(string path) => VaultPath.Create(path).Value;

    private sealed class Contribution(params CommandDescriptor[] commands) : ICommandContribution
    {
        public IEnumerable<CommandDescriptor> GetCommands() => commands;
    }

    private static CommandDescriptor Command(string id, string titleKey, string? gesture, Action? run = null, bool enabled = true) =>
        new(id, titleKey, "test", gesture, _ => enabled, (_, _) =>
        {
            run?.Invoke();
            return Task.FromResult(id == "fails" ? Result.Failure(new Error(ErrorCodes.CommandUnavailable, "x")) : Result.Success());
        });

    // --- Key bindings -------------------------------------------------------------------------------------------

    private static CommandRegistry Registry(params CommandDescriptor[] commands) => new([new Contribution(commands)]);

    [Fact]
    public void Bindings_ResolveDefaultsPerPlatform()
    {
        var registry = Registry(Command("a", "Commands.NewNote", "Mod+N"), Command("b", "Commands.OpenDailyNote", null));
        var windows = new KeyBindingService(registry, CommandPlatform.Windows);
        var mac = new KeyBindingService(registry, CommandPlatform.MacOS);

        windows.GestureFor("a")!.ToString(CommandPlatform.Windows).Should().Be("Ctrl+N");
        mac.Resolve(Gesture.Parse("Cmd+N", CommandPlatform.MacOS).Value)!.Id.Should().Be("a");
        windows.GestureFor("b").Should().BeNull();
        windows.Resolve(Gesture.Parse("Ctrl+Q", CommandPlatform.Windows).Value).Should().BeNull();
    }

    [Fact]
    public void Bindings_UserOverridesWin_AndCanUnassign()
    {
        var bindings = new KeyBindingService(Registry(Command("a", "x", "Mod+N"), Command("b", "y", "Mod+B")), CommandPlatform.Windows);

        bindings.ApplyOverrides(new Dictionary<string, string> { ["a"] = "Ctrl+Shift+N", ["b"] = string.Empty });

        bindings.GestureFor("a")!.ToString(CommandPlatform.Windows).Should().Be("Ctrl+Shift+N");
        bindings.Resolve(Gesture.Parse("Ctrl+N", CommandPlatform.Windows).Value).Should().BeNull();
        bindings.GestureFor("b").Should().BeNull();
    }

    [Fact]
    public void Bindings_DetectConflicts_AndPreferUserAssignments()
    {
        var bindings = new KeyBindingService(Registry(Command("a", "x", "Mod+N"), Command("b", "y", null)), CommandPlatform.Windows);

        var reassigned = bindings.Reassign("b", "Ctrl+N");

        reassigned.Value.Should().Equal(new Dictionary<string, string> { ["b"] = "Ctrl+N" });
        bindings.Conflicts.Should().ContainSingle().Which.Should().BeEquivalentTo(new { Gesture = "Ctrl+N", CommandIds = new[] { "a", "b" } });
        bindings.Resolve(Gesture.Parse("Ctrl+N", CommandPlatform.Windows).Value)!.Id.Should().Be("b");

        bindings.Reassign("b", "nope+").Error.Code.Should().Be(ErrorCodes.GestureInvalid);
        bindings.Reassign("zzz", "Ctrl+K").Error.Code.Should().Be(ErrorCodes.CommandNotFound);
        bindings.Reassign("b", null).Value.Should().BeEmpty("resetting removes the override");
        bindings.Conflicts.Should().BeEmpty();
    }

    // --- Quick switcher -----------------------------------------------------------------------------------------

    private readonly IQueryHandler<QuickSwitch, IReadOnlyList<QuickSwitchItem>> _switch = Substitute.For<IQueryHandler<QuickSwitch, IReadOnlyList<QuickSwitchItem>>>();
    private readonly ICommandHandler<CreateNote, VaultPath> _create = Substitute.For<ICommandHandler<CreateNote, VaultPath>>();

    private QuickSwitcherViewModel Switcher()
    {
        _switch.HandleAsync(Arg.Any<QuickSwitch>(), Arg.Any<CancellationToken>()).Returns(c => Result.Success<IReadOnlyList<QuickSwitchItem>>(c.Arg<QuickSwitch>().Text.Length == 0
            ? [new QuickSwitchItem(QuickSwitchKind.Note, P("Reciente.md"), "Reciente", [], 0)]
            : [new QuickSwitchItem(QuickSwitchKind.Create, null, c.Arg<QuickSwitch>().Text, [], 0), new QuickSwitchItem(QuickSwitchKind.Note, P("Ideas.md"), "Ideas", [0, 1], 3)]));
        return new QuickSwitcherViewModel(Services, Vault, _switch, _create, _navigator);
    }

    [Fact]
    public async Task QuickSwitcher_OpensWithRecentFiles()
    {
        var vm = Switcher();

        await vm.ShowAsync();

        vm.IsOpen.Should().BeTrue();
        vm.Items.Select(i => i.Display).Should().Equal("Reciente");
        vm.SelectedIndex.Should().Be(0);
    }

    [Fact]
    [Trait("Spec", "F-008.1")]
    public async Task QuickSwitcher_FirstOptionCreates_AndEnterCreatesAndOpens()
    {
        var vm = Switcher();
        _create.HandleAsync(Arg.Any<CreateNote>(), Arg.Any<CancellationToken>()).Returns(P("Idea nueva.md"));
        await vm.ShowAsync();

        vm.Text = "Idea nueva";
        await vm.WhenUpdatedAsync();
        vm.Items[0].Kind.Should().Be(QuickSwitchKind.Create);

        await vm.AcceptCommand.ExecuteAsync(QuickSwitchAccept.Open);

        await _create.Received(1).HandleAsync(new CreateNote(Vault, Title: "Idea nueva"), Arg.Any<CancellationToken>());
        await _navigator.Received(1).OpenAsync(new OpenRequest(P("Idea nueva.md")), Arg.Any<CancellationToken>());
        vm.IsOpen.Should().BeFalse();
    }

    [Fact]
    public async Task QuickSwitcher_OpensInANewTab_OrCreatesWithShiftEnter()
    {
        var vm = Switcher();
        _create.HandleAsync(Arg.Any<CreateNote>(), Arg.Any<CancellationToken>()).Returns(P("Ide.md"));
        await vm.ShowAsync();
        vm.Text = "Ide";
        await vm.WhenUpdatedAsync();

        vm.MoveSelection(1);
        await vm.AcceptCommand.ExecuteAsync(QuickSwitchAccept.NewTab);
        await vm.ShowAsync();
        vm.Text = "Ide";
        await vm.WhenUpdatedAsync();
        vm.MoveSelection(1);
        await vm.AcceptCommand.ExecuteAsync(QuickSwitchAccept.Create);

        await _navigator.Received(1).OpenAsync(new OpenRequest(P("Ideas.md"), NewTab: true), Arg.Any<CancellationToken>());
        await _create.Received(1).HandleAsync(new CreateNote(Vault, Title: "Ide"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QuickSwitcher_SelectionWrapsAround_AndEscapeCloses()
    {
        var vm = Switcher();
        await vm.ShowAsync();
        vm.Text = "x";
        await vm.WhenUpdatedAsync();

        vm.MoveSelection(-1);
        vm.SelectedIndex.Should().Be(1);
        vm.MoveSelection(1);
        vm.SelectedIndex.Should().Be(0);

        vm.CloseCommand.Execute(null);
        vm.IsOpen.Should().BeFalse();
    }

    [Fact]
    public async Task QuickSwitcher_CreateErrors_AreNotified_AndKeepItOpen()
    {
        var vm = Switcher();
        _create.HandleAsync(Arg.Any<CreateNote>(), Arg.Any<CancellationToken>()).Returns(new Error(DomainErrors.PathInvalidSegment, "x"));
        await vm.ShowAsync();
        vm.Text = "a:b";
        await vm.WhenUpdatedAsync();

        await vm.AcceptCommand.ExecuteAsync(QuickSwitchAccept.Open);

        _notifications.Shown.Single().Message.Should().Be("The name contains characters that are not allowed.");
        vm.IsOpen.Should().BeTrue();
    }

    // --- Command palette ----------------------------------------------------------------------------------------

    [Fact]
    [Trait("Spec", "F-008.2")]
    public async Task Gestures_RunTheirAvailableCommand()
    {
        var ran = new List<string>();
        var registry = Registry(
            Command("a", "Commands.NewNote", "Mod+N", () => ran.Add("a")),
            Command("b", "Commands.OpenDailyNote", "Mod+D", () => ran.Add("b"), enabled: false));
        var vm = new CommandPaletteViewModel(Services, registry, new KeyBindingService(registry, CommandPlatform.Windows), () => new CommandContext(Vault, null, _host));

        (await vm.ExecuteGestureAsync(new Gesture(GestureModifiers.Control, "N"), Ct)).Should().BeTrue();
        (await vm.ExecuteGestureAsync(new Gesture(GestureModifiers.Control, "D"), Ct)).Should().BeFalse("the command is unavailable");
        (await vm.ExecuteGestureAsync(new Gesture(GestureModifiers.Control, "Q"), Ct)).Should().BeFalse();

        ran.Should().Equal("a");
    }

    [Fact]
    [Trait("Spec", "F-008.2")]
    public async Task Palette_ListsAvailableCommandsWithShortcuts_AndRunsThem()
    {
        var ran = new List<string>();
        var registry = Registry(
            Command(CoreCommands.NewNote, "Commands.NewNote", "Mod+N", () => ran.Add("new")),
            Command(CoreCommands.OpenDailyNote, "Commands.OpenDailyNote", null, () => ran.Add("daily")),
            Command("disabled", "Commands.BookmarkActiveFile", null, enabled: false));
        var vm = new CommandPaletteViewModel(Services, registry, new KeyBindingService(registry, CommandPlatform.Windows), () => new CommandContext(Vault, null, _host));

        vm.Show();
        vm.Items.Select(i => (i.Title, i.Gesture)).Should().Equal(("New note", "Ctrl+N"), ("Open today's daily note", null));

        vm.Text = "daily";
        vm.Items.Select(i => i.Id).Should().Equal(CoreCommands.OpenDailyNote);
        vm.Items[0].Positions.Should().NotBeEmpty();
        await vm.ExecuteCommand.ExecuteAsync(null);

        ran.Should().Equal("daily");
        vm.IsOpen.Should().BeFalse();

        vm.Show();
        vm.Items.Select(i => i.Id).Should().Equal(CoreCommands.OpenDailyNote, CoreCommands.NewNote);
    }

    [Fact]
    public async Task Palette_ReportsCommandErrors()
    {
        var registry = Registry(Command("fails", "Commands.NewNote", null));
        var vm = new CommandPaletteViewModel(Services, registry, new KeyBindingService(registry, CommandPlatform.Windows), () => new CommandContext(Vault, null, _host));
        vm.Show();

        await vm.ExecuteCommand.ExecuteAsync(null);

        _notifications.Shown.Single().Message.Should().Be("The command is not available right now.");
    }
}

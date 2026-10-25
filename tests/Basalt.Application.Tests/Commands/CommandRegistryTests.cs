using Basalt.Application.Bookmarks;
using Basalt.Application.Commands;
using Basalt.Application.Errors;
using Basalt.Application.Notes;
using Basalt.Application.Templates;
using Basalt.Application.Tests.TestSupport;
using Basalt.Domain.Common;
using Basalt.Domain.Notes;
using Basalt.Domain.Vaults;
using Basalt.Testing.Fakes;
using static Basalt.Application.Tests.TestSupport.VaultFixture;

namespace Basalt.Application.Tests.Commands;

public sealed class CommandRegistryTests
{
    private sealed class Contribution(params CommandDescriptor[] commands) : ICommandContribution
    {
        public IEnumerable<CommandDescriptor> GetCommands() => commands;
    }

    private sealed class Host : ICommandHost
    {
        public List<(VaultPath Path, bool NewTab)> Opened { get; } = [];

        public Task OpenAsync(VaultPath path, bool newTab, CancellationToken ct)
        {
            Opened.Add((path, newTab));
            return Task.CompletedTask;
        }
    }

    private static CommandDescriptor Command(string id, string? gesture, Action? onRun = null, Func<CommandContext, bool>? canExecute = null) =>
        new(id, "Commands." + id, "test", gesture, canExecute ?? (_ => true), (_, _) =>
        {
            onRun?.Invoke();
            return Task.FromResult(Result.Success());
        });

    [Fact]
    [Trait("Spec", "F-008.2")]
    public async Task PaletteAndShortcut_RunTheSameCommand()
    {
        var runs = 0;
        var registry = new CommandRegistry([new Contribution(Command("view.toggle-reading", "Mod+E", () => runs++))]);
        var context = new CommandContext(null, null, new Host());

        var fromPalette = registry.Find("view.toggle-reading")!;
        var fromShortcut = registry.FindByGesture(Gesture.Parse("Ctrl+E", CommandPlatform.Windows).Value, CommandPlatform.Windows)!;
        await registry.ExecuteAsync(fromPalette.Id, context, Ct);
        await registry.ExecuteAsync(fromShortcut.Id, context, Ct);

        fromShortcut.Should().BeSameAs(fromPalette);
        runs.Should().Be(2);
        registry.FindByGesture(Gesture.Parse("Cmd+E", CommandPlatform.MacOS).Value, CommandPlatform.MacOS).Should().BeSameAs(fromPalette);
        registry.FindByGesture(Gesture.Parse("Ctrl+E", CommandPlatform.MacOS).Value, CommandPlatform.MacOS).Should().BeNull();
    }

    [Fact]
    public async Task UnknownOrUnavailableCommands_AreErrors()
    {
        var registry = new CommandRegistry([new Contribution(Command("a", null, canExecute: c => c.Vault is not null))]);
        var context = new CommandContext(null, null, new Host());

        (await registry.ExecuteAsync("nope", context, Ct)).Error.Code.Should().Be(ErrorCodes.CommandNotFound);
        (await registry.ExecuteAsync("a", context, Ct)).Error.Code.Should().Be(ErrorCodes.CommandUnavailable);
        registry.Find("nope").Should().BeNull();
    }

    [Fact]
    public async Task RegistryErrors_DescribeTheCommand()
    {
        var registry = new CommandRegistry([new Contribution(Command("a", null, canExecute: _ => false))]);
        var context = new CommandContext(null, null, new Host());

        var unknown = (await registry.ExecuteAsync("nope", context, Ct)).Error;
        var unavailable = (await registry.ExecuteAsync("a", context, Ct)).Error;

        unknown.Message.Should().Be("There is no such command.");
        unknown.Metadata!["command"].Should().Be("nope");
        unavailable.Message.Should().Be("The command cannot run now.");
        unavailable.Metadata!["command"].Should().Be("a");
    }

    [Fact]
    public async Task Registry_RejectsNullArguments()
    {
        FluentActions.Invoking(() => new CommandRegistry(null!)).Should().Throw<ArgumentNullException>();
        var registry = new CommandRegistry([]);
        await FluentActions.Awaiting(() => registry.ExecuteAsync("a", null!, Ct)).Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public void CoreCommands_ExposeTheirMetadata()
    {
        var core = new CoreCommands(null!, null!, null!);
        var commands = core.GetCommands().ToDictionary(c => c.Id);

        commands[CoreCommands.NewNote].TitleKey.Should().Be("Commands.NewNote");
        commands[CoreCommands.NewNote].Category.Should().Be("file");
        commands[CoreCommands.NewNote].DefaultGesture.Should().Be("Mod+N");
        commands[CoreCommands.OpenDailyNote].TitleKey.Should().Be("Commands.OpenDailyNote");
        commands[CoreCommands.OpenDailyNote].Category.Should().Be("daily-notes");
        commands[CoreCommands.OpenDailyNote].DefaultGesture.Should().Be("Mod+Shift+D");
        commands[CoreCommands.BookmarkActiveFile].TitleKey.Should().Be("Commands.BookmarkActiveFile");
        commands[CoreCommands.BookmarkActiveFile].Category.Should().Be("bookmarks");
        commands[CoreCommands.BookmarkActiveFile].DefaultGesture.Should().BeNull();
    }

    [Fact]
    public void DuplicateIds_AreAProgrammingError()
    {
        var create = () => new CommandRegistry([new Contribution(Command("a", null)), new Contribution(Command("a", null))]);

        create.Should().Throw<InvalidOperationException>().WithMessage("*'a'*");
    }

    [Fact]
    public void Commands_AreListedInContributionOrder()
    {
        new CommandRegistry([new Contribution(Command("b", null), Command("a", null)), new Contribution(Command("c", null))])
            .Commands.Select(c => c.Id).Should().Equal("b", "a", "c");
    }

    [Theory]
    [InlineData("Mod+N", CommandPlatform.Windows, "Ctrl+N")]
    [InlineData("Mod+N", CommandPlatform.MacOS, "Cmd+N")]
    [InlineData("mod+shift+t", CommandPlatform.Windows, "Ctrl+Shift+T")]
    [InlineData("Alt+Left", CommandPlatform.Windows, "Alt+Left")]
    [InlineData("Shift+Cmd+[", CommandPlatform.MacOS, "Cmd+Shift+[")]
    [InlineData("F2", CommandPlatform.Windows, "F2")]
    [InlineData("Ctrl+Alt+Shift+Cmd+K", CommandPlatform.MacOS, "Ctrl+Cmd+Alt+Shift+K")]
    public void Gestures_ParseAndNormalise(string text, CommandPlatform platform, string normalised)
    {
        Gesture.Parse(text, platform).Value.ToString(platform).Should().Be(normalised);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl+Shift")]
    [InlineData("Hyper+K")]
    [InlineData("A+B")]
    public void InvalidGestures_AreErrors(string text)
    {
        var error = Gesture.Parse(text, CommandPlatform.Windows).Error;

        error.Code.Should().Be(ErrorCodes.GestureInvalid);
        error.Message.Should().Be("The key gesture is not valid.");
        error.Metadata!["gesture"].Should().Be(text);
    }

    [Theory]
    [InlineData("Control+K", CommandPlatform.Windows, "Ctrl+K")]
    [InlineData("Command+K", CommandPlatform.MacOS, "Cmd+K")]
    [InlineData("Meta+K", CommandPlatform.MacOS, "Cmd+K")]
    [InlineData("Win+K", CommandPlatform.Windows, "Win+K")]
    [InlineData("Super+K", CommandPlatform.Windows, "Win+K")]
    [InlineData("Option+K", CommandPlatform.Windows, "Alt+K")]
    [InlineData("Shift+K", CommandPlatform.Windows, "Shift+K")]
    public void GestureModifierAliases_AreAccepted(string text, CommandPlatform platform, string normalised)
    {
        Gesture.Parse(text, platform).Value.ToString(platform).Should().Be(normalised);
    }

    [Theory]
    [InlineData("Alt+Left;Mod+[", CommandPlatform.Windows, "Alt+Left")]
    [InlineData("Alt+Left;Mod+[", CommandPlatform.MacOS, "Cmd+[")]
    public void DefaultGestures_PickThePlatformVariant(string text, CommandPlatform platform, string normalised)
    {
        Gesture.ParseDefault(text, platform).Value.ToString(platform).Should().Be(normalised);
    }

    [Fact]
    public void DefaultGestures_WithMoreThanTwoVariants_AreInvalid()
    {
        Gesture.ParseDefault("A;B;C", CommandPlatform.Windows).Error.Code.Should().Be(ErrorCodes.GestureInvalid);
    }

    [Fact]
    public void GestureParsing_RejectsNull()
    {
        FluentActions.Invoking(() => Gesture.Parse(null!, CommandPlatform.Windows)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => Gesture.ParseDefault(null!, CommandPlatform.Windows)).Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Gestures_PrintWithPlatformNames()
    {
        var gesture = Gesture.Parse("Ctrl+Cmd+K", CommandPlatform.MacOS).Value;

        gesture.ToString(CommandPlatform.Windows).Should().Be("Ctrl+Win+K");
        gesture.Should().Be(Gesture.Parse("Win+Ctrl+k", CommandPlatform.Windows).Value);
    }

    [Fact]
    public async Task CoreCommands_NewNote_DailyNote_AndBookmark()
    {
        await using var v = await VaultFixture.CreateAsync(new Dictionary<string, string> { ["Proyectos/A.md"] = "" });
        await new ProductivitySessionInitializer(new InMemoryVaultConfigStore(), new InMemoryRecentFilesStore(), v.Events).InitializeAsync(v.Session, Ct);
        var core = new CoreCommands(
            new CreateNoteHandler(v.Sessions, v.Events, v.Clock, v.Culture),
            new OpenDailyNoteHandler(v.Sessions, v.Events, v.Clock, v.Culture),
            new AddBookmarkHandler(v.Sessions));
        var registry = new CommandRegistry([core]);
        var host = new Host();
        var context = new CommandContext(Id, P("Proyectos/A.md"), host);

        (await registry.ExecuteAsync(CoreCommands.NewNote, context, Ct)).IsSuccess.Should().BeTrue();
        (await registry.ExecuteAsync(CoreCommands.OpenDailyNote, context, Ct)).IsSuccess.Should().BeTrue();
        (await registry.ExecuteAsync(CoreCommands.BookmarkActiveFile, context, Ct)).IsSuccess.Should().BeTrue();

        host.Opened.Should().Equal((P("Untitled.md"), false), (P("2026-10-19.md"), false));
        (await new ListBookmarksHandler(v.Sessions).HandleAsync(new ListBookmarks(Id), Ct)).Value.Document.Items.Should().Equal(Bookmark.ForFile("Proyectos/A.md"));
        registry.Find(CoreCommands.NewNote)!.DefaultGesture.Should().Be("Mod+N");

        var noVault = new CommandContext(null, null, host);
        registry.Commands.Should().OnlyContain(c => !c.CanExecute(noVault));
        registry.Find(CoreCommands.BookmarkActiveFile)!.CanExecute(context with { ActiveFile = null }).Should().BeFalse();
    }

    [Fact]
    public async Task CoreCommands_PropagateUseCaseErrors()
    {
        await using var v = await VaultFixture.CreateAsync(settings: VaultSettings.Default with { DailyNotes = new DailyNotesSettings(TemplatePath: "nada.md") });
        var core = new CoreCommands(
            new CreateNoteHandler(v.Sessions, v.Events, v.Clock, v.Culture),
            new OpenDailyNoteHandler(v.Sessions, v.Events, v.Clock, v.Culture),
            new AddBookmarkHandler(v.Sessions));
        var host = new Host();

        (await new CommandRegistry([core]).ExecuteAsync(CoreCommands.OpenDailyNote, new CommandContext(Id, null, host), Ct)).Error.Code.Should().Be(ErrorCodes.EntryNotFound);
        host.Opened.Should().BeEmpty();
    }
}

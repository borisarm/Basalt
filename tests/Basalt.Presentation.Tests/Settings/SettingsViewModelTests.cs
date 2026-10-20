using Basalt.Application.Abstractions;
using Basalt.Application.Commands;
using Basalt.Application.Ports.Platform;
using Basalt.Application.Settings;
using Basalt.Application.Vaults;
using Basalt.Domain.Common;
using Basalt.Domain.Vaults;
using Basalt.Presentation.Common;
using Basalt.Presentation.Input;
using Basalt.Presentation.Settings;
using Basalt.Testing.Fakes;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Basalt.Presentation.Tests.Settings;

public sealed class SettingsViewModelTests
{
    private static readonly VaultId Vault = new(Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001"));

    private sealed class FakeAppSettingsStore : IAppSettingsStore
    {
        public AppSettings Current { get; set; } = AppSettings.Default;

        public int Saves { get; private set; }

        public Task<AppSettings> GetAsync(CancellationToken ct) => Task.FromResult(Current);

        public Task<Result<Unit>> SaveAsync(AppSettings settings, CancellationToken ct)
        {
            Current = settings;
            Saves++;
            return Task.FromResult(Result.Success());
        }
    }

    private sealed class Contribution(params CommandDescriptor[] commands) : ICommandContribution
    {
        public IEnumerable<CommandDescriptor> GetCommands() => commands;
    }

    private readonly FakeAppSettingsStore _store = new();
    private readonly ICommandHandler<UpdateVaultSettings, Unit> _updateVault = Substitute.For<ICommandHandler<UpdateVaultSettings, Unit>>();
    private readonly IThemeService _theme = Substitute.For<IThemeService>();
    private readonly IPlatformPaths _paths = Substitute.For<IPlatformPaths>();
    private readonly Localization.ILocalizer _localizer = PresentationFakes.Localizer();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 19, 10, 0, 0, TimeSpan.Zero));
    private readonly KeyBindingService _bindings;
    private readonly CommandRegistry _registry;

    public SettingsViewModelTests()
    {
        _updateVault.HandleAsync(Arg.Any<UpdateVaultSettings>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        _paths.LogsDirectory.Returns("C:/datos/logs");
        var registry = _registry = new CommandRegistry([new Contribution(
            new CommandDescriptor(CoreCommands.NewNote, "Commands.NewNote", "file", "Mod+N", _ => true, (_, _) => Task.FromResult(Result.Success())),
            new CommandDescriptor(CoreCommands.OpenDailyNote, "Commands.OpenDailyNote", "daily", null, _ => true, (_, _) => Task.FromResult(Result.Success())))]);
        _bindings = new KeyBindingService(registry, CommandPlatform.Windows);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<SettingsViewModel> CreateAsync(VaultSessionInfo? vault = null)
    {
        var vm = new SettingsViewModel(
            new ViewModelServices(new ImmediateDispatcher(), new FakeNotificationService(), _localizer, _time),
            new SettingsDependencies(_store, _updateVault, _theme, _paths, _bindings, _registry),
            vault);
        await vm.LoadAsync(Ct);
        return vm;
    }

    [Fact]
    [Trait("Spec", "F-014")]
    public async Task Sections_DependOnHavingAVault()
    {
        (await CreateAsync()).Sections.Select(s => s.Id).Should().Equal("general", "editor", "appearance", "hotkeys", "about");
        (await CreateAsync(new VaultSessionInfo(Vault, "Notas", "C:/v", VaultSettings.Default, null))).Sections.Select(s => s.Id)
            .Should().Equal("general", "editor", "files-links", "appearance", "hotkeys", "daily-notes", "templates", "about");
    }

    [Fact]
    public async Task General_LanguageSwitchesTheUi_AndIsSaved()
    {
        var vm = await CreateAsync();

        await vm.General.SetLanguageAsync("es");

        _localizer.Get("Common.Cancel").Should().Be("Cancelar");
        _store.Current.Language.Should().Be("es");
        vm.General.Language.Should().Be("es");

        await vm.General.SetOpenLastVaultAsync(false);
        _store.Current.OpenLastVault.Should().BeFalse();
    }

    [Fact]
    public async Task Editor_ChangesAreSaved_AndClamped()
    {
        var vm = await CreateAsync();

        await vm.Editor.UpdateAsync(e => e with { FontSize = 100, ShowLineNumbers = true, FontFamily = "Fira Code" });

        _store.Current.Editor.Should().BeEquivalentTo(new { FontSize = EditorSettingsViewModel.MaxFontSize, ShowLineNumbers = true, FontFamily = "Fira Code" });
        vm.Editor.Settings.FontSize.Should().Be(EditorSettingsViewModel.MaxFontSize);
    }

    [Fact]
    public async Task Appearance_IsAppliedAndSaved()
    {
        var vm = await CreateAsync();

        await vm.Appearance.UpdateAsync(ThemeMode.Dark, "#7c3aed", 2);

        _theme.Received(1).Apply(ThemeMode.Dark, "#7c3aed", AppearanceSettingsViewModel.MinInterfaceFontSize);
        _store.Current.Theme.Should().Be(ThemeMode.Dark);
        _store.Current.InterfaceFontSize.Should().Be(AppearanceSettingsViewModel.MinInterfaceFontSize);
    }

    [Fact]
    public async Task VaultSections_UpdateTheVaultSettings()
    {
        var vm = await CreateAsync(new VaultSessionInfo(Vault, "Notas", "C:/v", VaultSettings.Default, null));

        await vm.FilesAndLinks!.UpdateAsync(s => s with { LinkFormat = LinkFormat.Markdown, AttachmentFolder = "Adjuntos" });
        await vm.DailyNotes!.UpdateAsync(new DailyNotesSettings("Diario", "dddd D MMMM YYYY", "Plantillas/Diario.md"));
        await vm.Templates!.SetFolderAsync("Plantillas");

        // Each change builds on the previous ones: the last update carries all three.
        var updates = _updateVault.ReceivedCalls().Select(c => ((UpdateVaultSettings)c.GetArguments()[0]!).Settings).ToList();
        updates.Should().HaveCount(3);
        updates[^1].Should().BeEquivalentTo(new
        {
            LinkFormat = LinkFormat.Markdown,
            AttachmentFolder = "Adjuntos",
            DailyNotes = new DailyNotesSettings("Diario", "dddd D MMMM YYYY", "Plantillas/Diario.md"),
            TemplatesFolder = "Plantillas",
        });
        vm.DailyNotes.Preview.Should().Be("Monday 19 October 2026");
    }

    [Fact]
    public async Task Hotkeys_ShowEffectiveGestures_AndPersistReassignments()
    {
        var vm = await CreateAsync();

        vm.Hotkeys.Rows.Select(r => (r.CommandId, r.Title, r.Gesture)).Should().Equal(
            (CoreCommands.NewNote, "New note", "Ctrl+N"), (CoreCommands.OpenDailyNote, "Open today's daily note", null));

        await vm.Hotkeys.AssignAsync(CoreCommands.OpenDailyNote, "Ctrl+N");
        vm.Hotkeys.Rows.Should().OnlyContain(r => r.HasConflict);
        _store.Current.Hotkeys.Should().Equal(new Dictionary<string, string> { [CoreCommands.OpenDailyNote] = "Ctrl+N" });

        (await vm.Hotkeys.AssignAsync(CoreCommands.OpenDailyNote, "Ctrl+")).IsFailure.Should().BeTrue();
        await vm.Hotkeys.ResetAsync(CoreCommands.OpenDailyNote);
        _store.Current.Hotkeys.Should().BeEmpty();
        vm.Hotkeys.Rows.Should().OnlyContain(r => !r.HasConflict);
    }

    [Fact]
    public async Task Hotkeys_SavedOverrides_AreAppliedOnLoad()
    {
        _store.Current = AppSettings.Default with { Hotkeys = new Dictionary<string, string> { [CoreCommands.NewNote] = "Ctrl+Alt+N" } };

        var vm = await CreateAsync();

        vm.Hotkeys.Rows[0].Gesture.Should().Be("Ctrl+Alt+N");
        vm.Hotkeys.Rows[0].IsCustom.Should().BeTrue();
    }

    [Fact]
    public async Task About_ShowsVersionAndLogs()
    {
        var vm = await CreateAsync();

        vm.About.Version.Should().NotBeNullOrWhiteSpace();
        vm.About.LogsDirectory.Should().Be("C:/datos/logs");
    }
}

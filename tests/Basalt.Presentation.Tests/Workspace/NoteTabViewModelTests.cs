using Basalt.Application.Errors;
using Basalt.Application.Events;
using Basalt.Application.Notes;
using Basalt.Application.Ports.FileSystem;
using Basalt.Domain.Common;
using Basalt.Domain.Notes;
using Basalt.Domain.Vaults;
using Basalt.Presentation.Common;
using Basalt.Presentation.Workspace;
using Basalt.Testing.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Basalt.Presentation.Tests.Workspace;

public sealed class NoteTabViewModelTests : IAsyncLifetime
{
    private static readonly VaultId Vault = new(Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001"));
    private static readonly VaultPath A = VaultPath.Create("A.md").Value;
    private static readonly VaultPath B = VaultPath.Create("B.md").Value;

    private readonly FakeTimeProvider _time = new();
    private readonly FakeNotes _notes = new();
    private readonly FakeEditor _editor = new();
    private readonly FakeNotificationService _notifications = new();
    private readonly InMemoryEventBus _events = new(NullLogger<InMemoryEventBus>.Instance);
    private readonly WorkspaceMessenger _messenger = new();
    private NoteTabViewModel _tab = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _notes.Write(A, "uno\ndos\ntres");
        _notes.Write(B, "otra");
        _tab = new NoteTabViewModel(
            new ViewModelServices(new ImmediateDispatcher(), _notifications, PresentationFakes.Localizer(), _time),
            new NoteTabUseCases(_notes, _notes),
            _events,
            _messenger,
            Vault);
        _tab.AttachEditor(_editor);
        (await _tab.ShowAsync(new OpenRequest(A), Ct)).IsSuccess.Should().BeTrue();
    }

    public async ValueTask DisposeAsync()
    {
        _tab.Dispose();
        await _events.DisposeAsync();
    }

    private async Task ExternalChangeAsync(VaultPath path, string text)
    {
        _notes.Write(path, text);
        await _events.PublishAsync(new VaultFileChangedExternally(Vault, new FileSystemChange(FileSystemChangeKind.Changed, path)), Ct);
        await _events.WaitForIdleAsync(Ct);
    }

    [Fact]
    public void Loading_ShowsTheNote()
    {
        _editor.Text.Should().Be("uno\ndos\ntres");
        _tab.Title.Should().Be("A");
        _tab.Path.Should().Be(A);
        _tab.IsDirty.Should().BeFalse();
        _tab.Kind.Should().Be("note");
    }

    [Fact]
    [Trait("Spec", "F-003.1")]
    [Trait("Spec", "NFR-024")]
    public async Task Autosave_TwoSecondsAfterTheLastChange()
    {
        _editor.Type(_tab, "uno\ndos\ntres!");
        _tab.IsDirty.Should().BeTrue();
        _time.Advance(TimeSpan.FromSeconds(1.5));
        _editor.Type(_tab, "uno\ndos\ntres!!");
        _time.Advance(TimeSpan.FromSeconds(1.5));
        _notes.Saves.Should().Be(0);

        _time.Advance(TimeSpan.FromSeconds(0.5));
        await _tab.WhenSavedAsync();

        _notes.Read(A).Should().Be("uno\ndos\ntres!!");
        _notes.Saves.Should().Be(1);
        _tab.IsDirty.Should().BeFalse();
    }

    [Fact]
    public async Task Flush_SavesImmediately_AndNavigatingAwayFlushes()
    {
        _editor.Type(_tab, "editado");

        await _tab.FlushAsync(Ct);
        _notes.Read(A).Should().Be("editado");

        _editor.Type(_tab, "editado 2");
        await _tab.ShowAsync(new OpenRequest(B), Ct);

        _notes.Read(A).Should().Be("editado 2");
        _editor.Text.Should().Be("otra");
        _tab.Title.Should().Be("B");
    }

    [Fact]
    public async Task UnchangedText_IsNotWritten()
    {
        _editor.Type(_tab, "uno\ndos\ntres");

        await _tab.FlushAsync(Ct);

        _notes.Saves.Should().Be(0);
        _tab.IsDirty.Should().BeFalse();
    }

    [Fact]
    [Trait("Spec", "F-003.2")]
    public async Task ExternalChange_WithoutLocalChanges_ReloadsKeepingTheCaretLine()
    {
        _editor.Caret = "uno\ndos\n".Length + 2;

        await ExternalChangeAsync(A, "cero\nuno\ndos\ntres");

        _editor.Text.Should().Be("cero\nuno\ndos\ntres");
        _editor.Caret.Should().Be("cero\nuno\ndos\n".Length + 2, "the caret stays at the same place of the line it was on");
        _tab.IsDirty.Should().BeFalse();
    }

    [Fact]
    public async Task OwnSaves_AndOtherFiles_DoNotReload()
    {
        _editor.Type(_tab, "mío");
        await _tab.FlushAsync(Ct);
        var calls = _editor.SetTextCalls;

        await _events.PublishAsync(new VaultFileChangedExternally(Vault, new FileSystemChange(FileSystemChangeKind.Changed, A)), Ct);
        await ExternalChangeAsync(B, "otra cosa");
        await _events.PublishAsync(new FilesChangedBySync(new VaultId(Guid.NewGuid()), [A]), Ct);
        await _events.WaitForIdleAsync(Ct);

        _editor.SetTextCalls.Should().Be(calls);
    }

    [Fact]
    [Trait("Spec", "F-003.3")]
    public async Task ExternalChange_WithLocalChanges_MergesCleanly()
    {
        _editor.Type(_tab, "uno\ndos\ntres\ncuatro");

        _notes.Write(A, "UNO\ndos\ntres");
        await _events.PublishAsync(new FilesChangedBySync(Vault, [A]), Ct);
        await _events.WaitForIdleAsync(Ct);

        _editor.Text.Should().Be("UNO\ndos\ntres\ncuatro");
        _tab.IsDirty.Should().BeTrue();
        _tab.Conflict.Should().BeNull();

        _time.Advance(TimeSpan.FromSeconds(2));
        await _tab.WhenSavedAsync();
        _notes.Read(A).Should().Be("UNO\ndos\ntres\ncuatro");
    }

    [Fact]
    [Trait("Spec", "F-003.3")]
    public async Task ConflictingChanges_OfferMineDiskOrDiff()
    {
        _editor.Type(_tab, "uno\nMÍO\ntres");

        await ExternalChangeAsync(A, "uno\nSUYO\ntres");

        _editor.Text.Should().Be("uno\nMÍO\ntres", "nothing is replaced until the user decides");
        _tab.Conflict.Should().BeEquivalentTo(new { Mine = "uno\nMÍO\ntres", Disk = "uno\nSUYO\ntres" });
        _time.Advance(TimeSpan.FromSeconds(10));
        _notes.Read(A).Should().Be("uno\nSUYO\ntres", "autosave waits while there is a conflict");

        ExternalConflictDiffRequested? diff = null;
        _messenger.Register<ExternalConflictDiffRequested>(this, (_, m) => diff = m);
        _tab.ShowConflictDiffCommand.Execute(null);
        diff.Should().BeEquivalentTo(new { Path = A, Mine = "uno\nMÍO\ntres", Disk = "uno\nSUYO\ntres" }, o => o.ComparingByValue<VaultPath>());

        await _tab.KeepMineCommand.ExecuteAsync(null);
        _notes.Read(A).Should().Be("uno\nMÍO\ntres");
        _tab.Conflict.Should().BeNull();
        _tab.IsDirty.Should().BeFalse();
    }

    [Fact]
    public async Task Conflict_UseDisk_ReplacesTheEditorText()
    {
        _editor.Type(_tab, "uno\nMÍO\ntres");
        await ExternalChangeAsync(A, "uno\nSUYO\ntres");

        _tab.UseDiskCommand.Execute(null);

        _editor.Text.Should().Be("uno\nSUYO\ntres");
        _tab.Conflict.Should().BeNull();
        _tab.IsDirty.Should().BeFalse();
    }

    [Fact]
    public async Task SaveRejectedAsModifiedExternally_MergesWithTheDiskVersion()
    {
        _editor.Type(_tab, "uno\ndos\ntres\ncuatro");
        _notes.Write(A, "UNO\ndos\ntres");

        await _tab.FlushAsync(Ct);
        await _tab.FlushAsync(Ct);

        _notes.Read(A).Should().Be("UNO\ndos\ntres\ncuatro");
    }

    [Fact]
    public async Task OtherSaveErrors_AreNotified_AndTheTabStaysDirty()
    {
        _editor.Type(_tab, "cambio");
        _notes.NextSaveError = new Error(ErrorCodes.FsIo, "disk full");

        await _tab.FlushAsync(Ct);

        _tab.IsDirty.Should().BeTrue();
        _notifications.Shown.Single().Message.Should().Be("The file could not be read or written.");
    }

    [Fact]
    public async Task MissingNotes_FailToOpen()
    {
        (await _tab.ShowAsync(new OpenRequest(VaultPath.Create("x.md").Value), Ct)).Error.Code.Should().Be(ErrorCodes.EntryNotFound);
    }

    [Fact]
    public void ReadingMode_Toggles()
    {
        _tab.ToggleReadingModeCommand.Execute(null);
        _tab.IsReadingMode.Should().BeTrue();
        _tab.ToggleReadingModeCommand.Execute(null);
        _tab.IsReadingMode.Should().BeFalse();
    }

    [Fact]
    public async Task TextOpenedBeforeTheEditorAttaches_IsShownOnAttach()
    {
        var tab = new NoteTabViewModel(
            new ViewModelServices(new ImmediateDispatcher(), _notifications, PresentationFakes.Localizer(), _time),
            new NoteTabUseCases(_notes, _notes),
            _events,
            _messenger,
            Vault);
        await tab.ShowAsync(new OpenRequest(B), Ct);
        var editor = new FakeEditor();

        tab.AttachEditor(editor);

        editor.Text.Should().Be("otra");
        tab.Dispose();
    }
}

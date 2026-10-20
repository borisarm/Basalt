using System.Globalization;
using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Basalt.Application.Abstractions;
using Basalt.Application.Events;
using Basalt.Application.Links;
using Basalt.Application.Notes;
using Basalt.Application.Tags;
using Basalt.Domain.Common;
using Basalt.Domain.Metadata;
using Basalt.Domain.Notes;
using Basalt.Domain.Sync;
using Basalt.Domain.Vaults;
using Basalt.Presentation.Common;
using Basalt.Presentation.Localization;
using Basalt.Presentation.Workspace;
using Basalt.UI.Editor;
using Basalt.UI.Localization;
using Basalt.UI.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Basalt.UI.Tests;

public sealed class EditorTests
{
    private static readonly VaultId Vault = new(Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001"));
    private static readonly VaultPath Note = VaultPath.Create("A.md").Value;

    private sealed class Notes(string text) : IQueryHandler<ReadNote, NoteDocument>, ICommandHandler<SaveNote, BlobSha>
    {
        public string Text { get; private set; } = text;

        public Task<Result<NoteDocument>> HandleAsync(ReadNote query, CancellationToken ct) =>
            Task.FromResult(Result.Success(new NoteDocument(query.Path, NoteContent.FromText(Text), NoteMetadata.Empty, GitBlobHasher.Compute(Encoding.UTF8.GetBytes(Text)))));

        public Task<Result<BlobSha>> HandleAsync(SaveNote command, CancellationToken ct)
        {
            Text = command.Text;
            return Task.FromResult(Result.Success(GitBlobHasher.Compute(Encoding.UTF8.GetBytes(Text))));
        }
    }

    private static async Task<(MarkdownEditorControl Editor, NoteTabViewModel Tab, Window Window, IQueryHandler<SuggestLinkTargets, IReadOnlyList<LinkSuggestion>> Links)> OpenAsync(string text)
    {
        var localizer = new ResxLocalizer(CultureInfo.GetCultureInfo("en"));
        LocalizationSource.Instance.Attach(localizer);
        var notes = new Notes(text);
        var links = Substitute.For<IQueryHandler<SuggestLinkTargets, IReadOnlyList<LinkSuggestion>>>();
        var tab = new NoteTabViewModel(
            new ViewModelServices(new AvaloniaUiDispatcher(), new NotificationService(new FakeTimeProvider()), localizer, new FakeTimeProvider()),
            new NoteTabUseCases(notes, notes),
            new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance),
            new WorkspaceMessenger(),
            Vault,
            new NoteEditingUseCases(links, Substitute.For<IQueryHandler<SuggestTags, IReadOnlyList<TagSuggestion>>>(), Substitute.For<ICommandHandler<EnsureBlockId, BlockId>>(), Substitute.For<ICommandHandler<ImportAttachment, ImportedAttachment>>()));
        await tab.ShowAsync(new OpenRequest(Note), TestContext.Current.CancellationToken);
        var editor = new MarkdownEditorControl { DataContext = tab };
        var window = new Window { Content = editor, Width = 800, Height = 600 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (editor, tab, window, links);
    }

    private static void Press(MarkdownEditorControl editor, Key key, KeyModifiers modifiers = KeyModifiers.None) =>
        editor.Editor.TextArea.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers });

    [Fact]
    [Trait("Spec", "F-003.1")]
    public Task Editor_ShowsTheNote_AndTypingMarksItDirty() => Headless.RunAsync(async () =>
    {
        var (editor, tab, window, _) = await OpenAsync("# Título\ntexto");

        editor.Editor.Text.Should().Be("# Título\ntexto");
        tab.IsDirty.Should().BeFalse();
        editor.Editor.Document.Insert(editor.Editor.Document.TextLength, "!");
        tab.IsDirty.Should().BeTrue();
        window.Close();
    });

    [Fact]
    [Trait("Spec", "F-003.4")]
    public Task Enter_ContinuesLists_AndCtrlB_WrapsTheSelection() => Headless.RunAsync(async () =>
    {
        var (editor, _, window, _) = await OpenAsync("- [ ] tarea");
        editor.Editor.CaretOffset = editor.Editor.Text.Length;

        Press(editor, Key.Enter);
        editor.Editor.Text.Should().Be("- [ ] tarea\n- [ ] ");

        editor.SetText("hola mundo", 0);
        editor.Editor.Select(5, 5);
        Press(editor, Key.B, KeyModifiers.Control);
        editor.Editor.Text.Should().Be("hola **mundo**");

        editor.Editor.Document.UndoStack.Undo();
        editor.Editor.Text.Should().Be("hola mundo", "each command is one undo step");
        window.Close();
    });

    [Fact]
    [Trait("Spec", "F-004.1")]
    public Task Completion_ListsAndInsertsSuggestions() => Headless.RunAsync(async () =>
    {
        var (editor, _, window, links) = await OpenAsync("ver [[pro]]");
        links.HandleAsync(Arg.Any<SuggestLinkTargets>(), Arg.Any<CancellationToken>()).Returns(Result.Success<IReadOnlyList<LinkSuggestion>>(
            [new LinkSuggestion(LinkSuggestionKind.Note, VaultPath.Create("Proyecto X.md").Value, "Proyecto X", "Proyecto X", null, null, [], 1)]));
        editor.Editor.CaretOffset = 9;

        (await editor.ShowCompletionsAsync()).Should().Be(1);
        var tab = (NoteTabViewModel)editor.DataContext!;
        var items = await tab.GetCompletionsAsync(new Presentation.Editor.TextBufferState(editor.Editor.Text, 9, 0), TestContext.Current.CancellationToken);
        await editor.AcceptAsync(items[0]);

        editor.Editor.Text.Should().Be("ver [[Proyecto X]]");
        editor.Editor.CaretOffset.Should().Be(16);
        window.Close();
    });

    [Fact]
    public Task Headings_Fold_AndReadingModeToggles() => Headless.RunAsync(async () =>
    {
        var (editor, tab, window, _) = await OpenAsync("# A\nuno\n## A1\ndos\n# B\ntres\n```\ncódigo\n```");

        // Sections A (with A1), A1 and B, and the fenced code block.
        editor.Foldings.Should().HaveCount(4);
        editor.Foldings.Select(f => editor.Editor.Document.GetLineByOffset(f.StartOffset).LineNumber).Should().BeEquivalentTo([1, 3, 5, 7]);

        Press(editor, Key.E, KeyModifiers.Control);
        tab.IsReadingMode.Should().BeTrue();
        window.Close();
    });
}

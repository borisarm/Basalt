using Basalt.Application.Abstractions;
using Basalt.Application.Errors;
using Basalt.Application.Events;
using Basalt.Application.Links;
using Basalt.Application.Notes;
using Basalt.Application.Tags;
using Basalt.Domain.Common;
using Basalt.Domain.Notes;
using Basalt.Domain.Vaults;
using Basalt.Presentation.Common;
using Basalt.Presentation.Editor;
using Basalt.Presentation.Workspace;
using Basalt.Testing.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Basalt.Presentation.Tests.Workspace;

public sealed class NoteTabEditingTests : IAsyncLifetime
{
    private static readonly VaultId Vault = new(Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001"));
    private static readonly VaultPath Note = VaultPath.Create("Notas/A.md").Value;

    private readonly FakeNotes _notes = new();
    private readonly FakeNotificationService _notifications = new();
    private readonly InMemoryEventBus _events = new(NullLogger<InMemoryEventBus>.Instance);
    private readonly WorkspaceMessenger _messenger = new();
    private readonly IQueryHandler<SuggestLinkTargets, IReadOnlyList<LinkSuggestion>> _links = Substitute.For<IQueryHandler<SuggestLinkTargets, IReadOnlyList<LinkSuggestion>>>();
    private readonly IQueryHandler<SuggestTags, IReadOnlyList<TagSuggestion>> _tags = Substitute.For<IQueryHandler<SuggestTags, IReadOnlyList<TagSuggestion>>>();
    private readonly ICommandHandler<EnsureBlockId, BlockId> _blockId = Substitute.For<ICommandHandler<EnsureBlockId, BlockId>>();
    private readonly ICommandHandler<ImportAttachment, ImportedAttachment> _import = Substitute.For<ICommandHandler<ImportAttachment, ImportedAttachment>>();
    private NoteTabViewModel _tab = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static VaultPath P(string path) => VaultPath.Create(path).Value;

    public async ValueTask InitializeAsync()
    {
        _notes.Write(Note, "texto");
        _tab = new NoteTabViewModel(
            new ViewModelServices(new ImmediateDispatcher(), _notifications, PresentationFakes.Localizer(), new FakeTimeProvider(new DateTimeOffset(2026, 10, 19, 15, 42, 33, TimeSpan.Zero))),
            new NoteTabUseCases(_notes, _notes),
            _events,
            _messenger,
            Vault,
            new NoteEditingUseCases(_links, _tags, _blockId, _import));
        await _tab.ShowAsync(new OpenRequest(Note), Ct);
    }

    public async ValueTask DisposeAsync()
    {
        _tab.Dispose();
        await _events.DisposeAsync();
    }

    private static LinkSuggestion Link(LinkSuggestionKind kind, string display, string target, string? subpath = null, string? alias = null, int? line = null) =>
        new(kind, P("Proyecto X.md"), display, target, subpath, alias, [], 1, line);

    [Fact]
    [Trait("Spec", "F-004.1")]
    public async Task LinkCompletions_ReplaceThePartial()
    {
        _links.HandleAsync(new SuggestLinkTargets(Vault, Note, "pro"), Arg.Any<CancellationToken>()).Returns(Result.Success<IReadOnlyList<LinkSuggestion>>(
        [
            Link(LinkSuggestionKind.Note, "Proyecto X", "Proyecto X"),
            Link(LinkSuggestionKind.Alias, "PX", "Proyecto X", alias: "PX"),
        ]));
        var state = new TextBufferState("ver [[pro]]", 9, 0);

        var items = await _tab.GetCompletionsAsync(state, Ct);
        var edit = await _tab.AcceptCompletionAsync(state, items[1], Ct);

        items.Select(i => i.Display).Should().Equal("Proyecto X", "PX");
        edit.Value.Should().Be(new TextEdit(6, 3, "Proyecto X|PX"));
        (await _tab.AcceptCompletionAsync(state, items[0], Ct)).Value.Should().Be(new TextEdit(6, 3, "Proyecto X"));
    }

    [Fact]
    [Trait("Spec", "F-004.2")]
    public async Task HeadingCompletions_InsertTheSubpath()
    {
        _links.HandleAsync(Arg.Any<SuggestLinkTargets>(), Arg.Any<CancellationToken>()).Returns(Result.Success<IReadOnlyList<LinkSuggestion>>(
            [Link(LinkSuggestionKind.Heading, "Riesgos", "Proyecto X", subpath: "Riesgos", line: 3)]));
        var state = new TextBufferState("[[Proyecto X#ri", 15, 0);

        var items = await _tab.GetCompletionsAsync(state, Ct);

        (await _tab.AcceptCompletionAsync(state, items[0], Ct)).Value.Should().Be(new TextEdit(2, 13, "Proyecto X#Riesgos"));
    }

    [Fact]
    [Trait("Spec", "F-004.3")]
    public async Task BlockCompletions_WithoutId_CreateOne()
    {
        _links.HandleAsync(Arg.Any<SuggestLinkTargets>(), Arg.Any<CancellationToken>()).Returns(Result.Success<IReadOnlyList<LinkSuggestion>>(
            [Link(LinkSuggestionKind.Block, "Primer párrafo", "Proyecto X", line: 5), Link(LinkSuggestionKind.Block, "Otro", "Proyecto X", subpath: "^ab12", line: 7)]));
        _blockId.HandleAsync(new EnsureBlockId(Vault, P("Proyecto X.md"), 5), Arg.Any<CancellationToken>()).Returns(BlockId.Create("x1y2z3").Value);
        var state = new TextBufferState("[[Proyecto X#^", 14, 0);

        var items = await _tab.GetCompletionsAsync(state, Ct);

        (await _tab.AcceptCompletionAsync(state, items[0], Ct)).Value.Should().Be(new TextEdit(2, 12, "Proyecto X#^x1y2z3"));
        (await _tab.AcceptCompletionAsync(state, items[1], Ct)).Value.Should().Be(new TextEdit(2, 12, "Proyecto X#^ab12"));
    }

    [Fact]
    public async Task TagCompletions()
    {
        _tags.HandleAsync(new SuggestTags(Vault, "tra"), Arg.Any<CancellationToken>()).Returns(Result.Success<IReadOnlyList<TagSuggestion>>(
            [new TagSuggestion("trabajo", 3, [0, 1, 2], 2)]));
        var state = new TextBufferState("a #tra", 6, 0);

        var items = await _tab.GetCompletionsAsync(state, Ct);

        items.Single().Should().BeEquivalentTo(new { Display = "trabajo", Detail = "3", Kind = CompletionKind.Tag });
        (await _tab.AcceptCompletionAsync(state, items[0], Ct)).Value.Should().Be(new TextEdit(3, 3, "trabajo"));
    }

    [Fact]
    public async Task NoContext_NoCompletions()
    {
        (await _tab.GetCompletionsAsync(new TextBufferState("texto", 5, 0), Ct)).Should().BeEmpty();
    }

    [Fact]
    [Trait("Spec", "F-003.5")]
    public async Task PastingAnImage_ImportsItAndReturnsTheEmbed()
    {
        _import.HandleAsync(Arg.Any<ImportAttachment>(), Arg.Any<CancellationToken>()).Returns(new ImportedAttachment(P("Adjuntos/Pasted image 20261019154233.png"), "![[Pasted image 20261019154233.png]]"));

        var embed = await _tab.PasteImageAsync([1, 2, 3], "png", Ct);

        embed.Value.Should().Be("![[Pasted image 20261019154233.png]]");
        await _import.Received(1).HandleAsync(Arg.Is<ImportAttachment>(i => i.SourceNote == Note && i.FileName == "Pasted image 20261019154233.png" && i.Bytes.Length == 3), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WithoutEditingUseCases_CompletionsAreEmpty_AndPasteIsUnavailable()
    {
        using var plain = new NoteTabViewModel(
            new ViewModelServices(new ImmediateDispatcher(), _notifications, PresentationFakes.Localizer(), new FakeTimeProvider()),
            new NoteTabUseCases(_notes, _notes), _events, _messenger, Vault);
        await plain.ShowAsync(new OpenRequest(Note), Ct);

        (await plain.GetCompletionsAsync(new TextBufferState("[[a", 3, 0), Ct)).Should().BeEmpty();
        (await plain.PasteImageAsync([1], "png", Ct)).Error.Code.Should().Be(ErrorCodes.CommandUnavailable);
        plain.Editing.Should().NotBeNull();
    }

    [Fact]
    public void CaretLine_IsAnnounced()
    {
        CaretLineChanged? announced = null;
        _messenger.Register<CaretLineChanged>(this, (_, m) => announced = m);

        _tab.OnCaretLineChanged(4);

        announced.Should().Be(new CaretLineChanged(Note, 4));
    }
}

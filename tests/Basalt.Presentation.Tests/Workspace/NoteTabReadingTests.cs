using Basalt.Application.Abstractions;
using Basalt.Application.Events;
using Basalt.Application.Notes;
using Basalt.Application.Ports.Markdown;
using Basalt.Domain.Common;
using Basalt.Domain.Notes;
using Basalt.Domain.Vaults;
using Basalt.Presentation.Common;
using Basalt.Presentation.Workspace;
using Basalt.Testing.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Basalt.Presentation.Tests.Workspace;

public sealed class NoteTabReadingTests : IAsyncLifetime
{
    private static readonly VaultId Vault = new(Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001"));
    private static readonly VaultPath Note = VaultPath.Create("A.md").Value;

    private readonly FakeNotes _notes = new();
    private readonly InMemoryEventBus _events = new(NullLogger<InMemoryEventBus>.Instance);
    private readonly WorkspaceMessenger _messenger = new();
    private readonly IQueryHandler<RenderNote, RenderDocument> _render = Substitute.For<IQueryHandler<RenderNote, RenderDocument>>();
    private readonly IQueryHandler<ResolveLink, VaultPath?> _resolve = Substitute.For<IQueryHandler<ResolveLink, VaultPath?>>();
    private readonly IQueryHandler<GetEmbed, EmbedContent> _embed = Substitute.For<IQueryHandler<GetEmbed, EmbedContent>>();
    private readonly IQueryHandler<ReadAttachment, byte[]> _attachment = Substitute.For<IQueryHandler<ReadAttachment, byte[]>>();
    private readonly ICommandHandler<ToggleTask, Unit> _toggle = Substitute.For<ICommandHandler<ToggleTask, Unit>>();
    private readonly ICommandHandler<CreateNote, VaultPath> _create = Substitute.For<ICommandHandler<CreateNote, VaultPath>>();
    private readonly FakeEditor _editor = new();
    private NoteTabViewModel _tab = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static VaultPath P(string path) => VaultPath.Create(path).Value;

    public async ValueTask InitializeAsync()
    {
        _notes.Write(Note, "- [ ] tarea");
        _render.HandleAsync(Arg.Any<RenderNote>(), Arg.Any<CancellationToken>()).Returns(c => Result.Success(new RenderDocument([new ParagraphBlock([new TextInline(c.Arg<RenderNote>().Text)])])));
        _tab = new NoteTabViewModel(
            new ViewModelServices(new ImmediateDispatcher(), new FakeNotificationService(), PresentationFakes.Localizer(), new FakeTimeProvider()),
            new NoteTabUseCases(_notes, _notes),
            _events,
            _messenger,
            Vault,
            reading: new NoteReadingUseCases(_render, _resolve, _embed, _attachment, _toggle, _create));
        _tab.AttachEditor(_editor);
        await _tab.ShowAsync(new OpenRequest(Note), Ct);
    }

    public async ValueTask DisposeAsync()
    {
        _tab.Dispose();
        await _events.DisposeAsync();
    }

    private static string Text(RenderDocument document) => ((TextInline)((ParagraphBlock)document.Blocks[0]).Inlines[0]).Text;

    [Fact]
    [Trait("Spec", "UI-6")]
    public async Task ReadingMode_RendersTheCurrentText()
    {
        _editor.Type(_tab, "- [ ] tarea editada");

        await _tab.ToggleReadingModeCommand.ExecuteAsync(null);

        _tab.IsReadingMode.Should().BeTrue();
        Text(_tab.Rendered!).Should().Be("- [ ] tarea editada");
        await _tab.ToggleReadingModeCommand.ExecuteAsync(null);
        _tab.IsReadingMode.Should().BeFalse();
    }

    [Fact]
    public async Task Links_NavigateOrCreateTheMissingNote()
    {
        _resolve.HandleAsync(new ResolveLink(Vault, Note, "B"), Arg.Any<CancellationToken>()).Returns(Result.Success<VaultPath?>(P("B.md")));
        _resolve.HandleAsync(new ResolveLink(Vault, Note, "Nueva"), Arg.Any<CancellationToken>()).Returns(Result.Success<VaultPath?>(null));
        _create.HandleAsync(new CreateNote(Vault, Title: "Nueva"), Arg.Any<CancellationToken>()).Returns(P("Nueva.md"));
        var requests = new List<OpenRequest>();
        _messenger.Register<NavigateRequested>(this, (_, m) => requests.Add(m.Request));

        await _tab.OpenLinkAsync("B", "Tema", newTab: true, Ct);
        await _tab.OpenLinkAsync("Nueva", null, newTab: false, Ct);

        requests.Should().Equal(new OpenRequest(P("B.md"), NewTab: true, Subpath: "Tema"), new OpenRequest(P("Nueva.md")));
    }

    [Fact]
    public async Task Embeds_PassTheNoteAsAncestor_AndAttachmentsAreRead()
    {
        _embed.HandleAsync(Arg.Any<GetEmbed>(), Arg.Any<CancellationToken>()).Returns(new MissingEmbed("x"));
        _attachment.HandleAsync(new ReadAttachment(Vault, P("img.png")), Arg.Any<CancellationToken>()).Returns(new byte[] { 1, 2 });

        (await _tab.GetEmbedAsync("x", null, [P("Outer.md")], Ct)).Should().Be(new MissingEmbed("x"));
        (await _tab.ReadAttachmentAsync(P("img.png"), Ct)).Should().Equal(1, 2);

        await _embed.Received(1).HandleAsync(Arg.Is<GetEmbed>(e => e.SourcePath == Note && e.Ancestors.SequenceEqual(new[] { P("Outer.md"), Note })), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait("Spec", "UI-6")]
    public async Task TickingATask_SavesFirst_TogglesAndReRenders()
    {
        _editor.Type(_tab, "- [ ] tarea\notra línea");
        _toggle.HandleAsync(Arg.Any<ToggleTask>(), Arg.Any<CancellationToken>()).Returns(c =>
        {
            _notes.Write(Note, "- [x] tarea\notra línea");
            return Result.Success();
        });
        await _tab.ToggleReadingModeCommand.ExecuteAsync(null);

        await _tab.ToggleTaskAsync(0, Ct);

        _notes.Saves.Should().Be(1, "pending edits are saved before toggling");
        await _toggle.Received(1).HandleAsync(new ToggleTask(Vault, Note, 0), Arg.Any<CancellationToken>());
        _editor.Text.Should().Be("- [x] tarea\notra línea");
        Text(_tab.Rendered!).Should().Be("- [x] tarea\notra línea");
    }
}

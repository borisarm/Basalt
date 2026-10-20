using System.Globalization;
using System.Text;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Basalt.Application.Abstractions;
using Basalt.Application.Events;
using Basalt.Application.Notes;
using Basalt.Application.Ports.Markdown;
using Basalt.Domain.Common;
using Basalt.Domain.Metadata;
using Basalt.Domain.Notes;
using Basalt.Domain.Sync;
using Basalt.Domain.Vaults;
using Basalt.Presentation.Common;
using Basalt.Presentation.Localization;
using Basalt.Presentation.Workspace;
using Basalt.UI.Localization;
using Basalt.UI.Reading;
using Basalt.UI.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Basalt.UI.Tests;

public sealed class ReadingViewTests
{
    private static readonly VaultId Vault = new(Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001"));
    private static readonly VaultPath Note = VaultPath.Create("A.md").Value;

    private static VaultPath P(string path) => VaultPath.Create(path).Value;

    private static byte[] Png(int width, int height)
    {
        using var bitmap = new WriteableBitmap(new Avalonia.PixelSize(width, height), new Avalonia.Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var stream = new MemoryStream();
        bitmap.Save(stream, new PngBitmapEncoderOptions());
        return stream.ToArray();
    }

    private sealed class Notes(string text) : IQueryHandler<ReadNote, NoteDocument>, ICommandHandler<SaveNote, BlobSha>
    {
        public string Text { get; set; } = text;

        public Task<Result<NoteDocument>> HandleAsync(ReadNote query, CancellationToken ct) =>
            Task.FromResult(Result.Success(new NoteDocument(query.Path, NoteContent.FromText(Text), NoteMetadata.Empty, GitBlobHasher.Compute(Encoding.UTF8.GetBytes(Text)))));

        public Task<Result<BlobSha>> HandleAsync(SaveNote command, CancellationToken ct)
        {
            Text = command.Text;
            return Task.FromResult(Result.Success(GitBlobHasher.Compute(Encoding.UTF8.GetBytes(Text))));
        }
    }

    [Fact]
    public Task ImageCache_ReusesDecodedImages_AndIgnoresInvalidBytes() => Headless.RunAsync(async () =>
    {
        using var cache = new ImageCache();
        var loads = 0;
        var png = Png(4, 2);

        var first = await cache.GetAsync(P("a.png"), () => { loads++; return Task.FromResult<byte[]?>(png); });
        var second = await cache.GetAsync(P("a.png"), () => { loads++; return Task.FromResult<byte[]?>(png); });
        var broken = await cache.GetAsync(P("b.png"), () => Task.FromResult<byte[]?>([1, 2, 3]));

        first.Should().BeSameAs(second);
        loads.Should().Be(1);
        broken.Should().BeNull();
        cache.Count.Should().Be(1);
        cache.Size.Should().Be(4 * 2 * 4);
    });

    [Fact]
    [Trait("Spec", "UI-6")]
    public Task ReadingMode_RendersBlocks_EmbedsImages_AndTicksTasks() => Headless.RunAsync(async () =>
    {
        var localizer = new ResxLocalizer(CultureInfo.GetCultureInfo("en"));
        LocalizationSource.Instance.Attach(localizer);
        var notes = new Notes("# Título\n- [ ] tarea\n![[img.png]]");
        var toggle = Substitute.For<ICommandHandler<ToggleTask, Unit>>();
        toggle.HandleAsync(Arg.Any<ToggleTask>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            notes.Text = "# Título\n- [x] tarea\n![[img.png]]";
            return Result.Success();
        });
        var embed = Substitute.For<IQueryHandler<GetEmbed, EmbedContent>>();
        embed.HandleAsync(Arg.Any<GetEmbed>(), Arg.Any<CancellationToken>()).Returns(new ImageEmbed(P("img.png")));
        var attachment = Substitute.For<IQueryHandler<ReadAttachment, byte[]>>();
        attachment.HandleAsync(Arg.Any<ReadAttachment>(), Arg.Any<CancellationToken>()).Returns(Png(3, 3));
        var render = Substitute.For<IQueryHandler<RenderNote, RenderDocument>>();
        render.HandleAsync(Arg.Any<RenderNote>(), Arg.Any<CancellationToken>()).Returns(c => Result.Success(new RenderDocument(
        [
            new HeadingBlock(1, [new TextInline("Título")]) { Line = 0 },
            new ListBlock(false, 1, [new ListItemBlock([new ParagraphBlock([new TextInline("tarea")])], c.Arg<RenderNote>().Text.Contains("[x]", StringComparison.Ordinal) ? 'x' : ' ') { Line = 1 }]) { Line = 1 },
            new EmbedBlock(new EmbedInline("img.png", null, null, 120, null)) { Line = 2 },
        ])));
        var tab = new NoteTabViewModel(
            new ViewModelServices(new AvaloniaUiDispatcher(), new NotificationService(new FakeTimeProvider()), localizer, new FakeTimeProvider()),
            new NoteTabUseCases(notes, notes),
            new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance),
            new WorkspaceMessenger(),
            Vault,
            reading: new NoteReadingUseCases(render, Substitute.For<IQueryHandler<ResolveLink, VaultPath?>>(), embed, attachment, toggle, Substitute.For<ICommandHandler<CreateNote, VaultPath>>()));
        await tab.ShowAsync(new OpenRequest(Note), TestContext.Current.CancellationToken);
        var view = new Views.NoteTabView { DataContext = tab };
        var window = new Window { Content = view, Width = 800, Height = 600 };
        window.Show();

        await tab.ToggleReadingModeCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Dispatcher.UIThread.RunJobs();

        var reading = view.FindControl<RenderDocumentView>("ReadingView")!;
        reading.IsVisible.Should().BeTrue();
        var texts = reading.GetVisualDescendants().OfType<SelectableTextBlock>().Select(t => t.Inlines!.Count > 0 ? string.Concat(t.Inlines.OfType<Avalonia.Controls.Documents.Run>().Select(r => r.Text)) : t.Text).ToList();
        texts.Should().Contain(["Título", "tarea"]);
        var image = reading.GetVisualDescendants().OfType<Image>().Should().ContainSingle().Subject;
        image.Width.Should().Be(120);
        image.Source.Should().NotBeNull();

        reading.GetVisualDescendants().OfType<CheckBox>().Single().IsChecked = true;
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Dispatcher.UIThread.RunJobs();

        await toggle.Received(1).HandleAsync(new ToggleTask(Vault, Note, 1), Arg.Any<CancellationToken>());
        reading.GetVisualDescendants().OfType<CheckBox>().Single().IsChecked.Should().BeTrue();
        window.Close();
    });
}

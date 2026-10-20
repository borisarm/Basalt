using Basalt.Presentation.Common;
using Basalt.Testing.Fakes;

namespace Basalt.Presentation.Tests;

public sealed class WorkspaceMessengerTests
{
    private sealed record Ping(int Value);

    private sealed class Recipient
    {
        public List<int> Received { get; } = [];
    }

    [Fact]
    public void RegisteredRecipients_ReceiveMessages_UntilUnregistered()
    {
        var messenger = new WorkspaceMessenger();
        var recipient = new Recipient();
        messenger.Register<Ping>(recipient, (r, m) => ((Recipient)r).Received.Add(m.Value));

        messenger.Send(new Ping(1));
        messenger.UnregisterAll(recipient);
        messenger.Send(new Ping(2));

        recipient.Received.Should().Equal(1);
    }

    [Fact]
    public void SeparateMessengers_AreIsolated()
    {
        var first = new WorkspaceMessenger();
        var recipient = new Recipient();
        first.Register<Ping>(recipient, (r, m) => ((Recipient)r).Received.Add(m.Value));

        new WorkspaceMessenger().Send(new Ping(1));

        recipient.Received.Should().BeEmpty();
    }

    [Fact]
    public async Task ImmediateDispatcher_RunsInline()
    {
        var dispatcher = new ImmediateDispatcher();
        var ran = 0;

        dispatcher.Post(() => ran++);
        await dispatcher.InvokeAsync(() => ran++);
        await dispatcher.InvokeAsync(() =>
        {
            ran++;
            return Task.CompletedTask;
        });

        dispatcher.CheckAccess().Should().BeTrue();
        ran.Should().Be(3);
    }

    [Fact]
    public async Task FakeDialogs_AnswerAsScripted_AndRecordRequests()
    {
        var dialogs = new FakeDialogService { ConfirmAnswer = false, PromptAnswer = "Nueva", FolderAnswer = "C:/v" };

        (await dialogs.ConfirmAsync(new ConfirmRequest("t", "m", "Borrar", IsDestructive: true), Ct)).Should().BeFalse();
        (await dialogs.PromptAsync(new PromptRequest("t", "Nombre", "x"), Ct)).Should().Be("Nueva");
        (await dialogs.PickFolderAsync("Elegir", Ct)).Should().Be("C:/v");

        dialogs.Requests.Should().HaveCount(3);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}

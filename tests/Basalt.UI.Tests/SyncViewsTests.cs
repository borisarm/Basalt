using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Basalt.Application.Abstractions;
using Basalt.Application.Accounts;
using Basalt.Application.Ports.Auth;
using Basalt.Domain.Accounts;
using Basalt.Domain.Common;
using Basalt.Presentation.Common;
using Basalt.Presentation.Sync;
using Basalt.UI.Localization;
using Basalt.UI.Views;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Basalt.UI.Tests;

public sealed class SyncViewsTests
{
    private static async Task SettleAsync()
    {
        for (var i = 0; i < 20; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Yield();
        }

        Dispatcher.UIThread.RunJobs();
    }

    [Fact]
    [Trait("Spec", "F-015.1")]
    public Task SignIn_ShowsTheDeviceCode_AndWaits() => Headless.RunAsync(async () =>
    {
        var localizer = new Basalt.Presentation.Localization.ResxLocalizer(System.Globalization.CultureInfo.GetCultureInfo("en"));
        LocalizationSource.Instance.Attach(localizer);
        var services = new ViewModelServices(
            new Basalt.UI.Services.AvaloniaUiDispatcher(),
            new Basalt.UI.Services.NotificationService(new FakeTimeProvider()),
            localizer,
            new FakeTimeProvider());
        var start = Substitute.For<ICommandHandler<StartGitHubSignIn, DeviceCodeChallenge>>();
        var complete = Substitute.For<ICommandHandler<CompleteGitHubSignIn, GitHubAccount>>();
        var pending = new TaskCompletionSource<Result<GitHubAccount>>();
        start.HandleAsync(Arg.Any<StartGitHubSignIn>(), Arg.Any<CancellationToken>())
            .Returns(Result<DeviceCodeChallenge>.Success(new DeviceCodeChallenge(
                GitHubHost.GitHubDotCom, "device-code", "ABCD-1234", "https://github.com/login/device",
                TimeSpan.FromSeconds(5), DateTimeOffset.UtcNow.AddMinutes(15))));
        complete.HandleAsync(Arg.Any<CompleteGitHubSignIn>(), Arg.Any<CancellationToken>())
            .Returns(_ => pending.Task);
        var vm = new GitHubSignInViewModel(services, new GitHubSignInUseCases(start, complete));
        var window = new SignInWindow(vm);
        window.Show();

        await SettleAsync();

        window.GetVisualDescendants().OfType<SelectableTextBlock>().First(t => t.Name == "UserCode").Text.Should().Be("ABCD-1234");
        window.GetVisualDescendants().OfType<TextBlock>().Should().Contain(t => t.Text == "Waiting for authorization…");

        window.Close();
        (await window.Result).Should().BeFalse();
    });

    [Fact]
    [Trait("Spec", "F-015.6")]
    public Task DiffWindow_ShowsTheAlignedRows() => Headless.RunAsync(async () =>
    {
        var window = new DiffWindow(new Basalt.Presentation.Diff.DiffRequest(
            "Differences", "Mine", "On disk", "a\nold\n", "a\nnew\n"));
        window.Show();
        await SettleAsync();

        var view = window.GetVisualDescendants().OfType<DiffView>().Single();
        view.Document.Should().HaveCount(2);
        view.Document!.Select(row => row.Kind).Should().Equal(
            Basalt.Presentation.Diff.DiffLineKind.Equal,
            Basalt.Presentation.Diff.DiffLineKind.Changed);

        window.Close();
    });
}

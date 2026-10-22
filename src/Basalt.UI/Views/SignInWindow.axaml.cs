using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Basalt.Domain.Accounts;
using Basalt.Presentation.Sync;

namespace Basalt.UI.Views;

/// <summary>GitHub device-flow dialog (F-015.1): shows the code, waits and reports whether an account was added.</summary>
public partial class SignInWindow : Window
{
    private readonly GitHubSignInViewModel? _viewModel;
    private readonly TaskCompletionSource<bool> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public SignInWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public SignInWindow(GitHubSignInViewModel viewModel)
        : this()
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.SignedIn += OnSignedIn;
        Opened += async (_, _) => await viewModel.StartAsync(GitHubHost.GitHubDotCom).ConfigureAwait(true);
        Closed += (_, _) =>
        {
            viewModel.SignedIn -= OnSignedIn;
            _result.TrySetResult(viewModel.Account is not null);
        };
    }

    /// <summary>Completes with <c>true</c> when the user authorized the app.</summary>
    public Task<bool> Result => _result.Task;

    private void OnSignedIn(object? sender, GitHubAccount account)
    {
        _result.TrySetResult(true);
        Close();
    }

    private async void OnOpenGitHub(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is not { } viewModel || viewModel.UserCode is not { } code)
        {
            return;
        }

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
        {
            await clipboard.SetTextAsync(code).ConfigureAwait(true);
        }

        if (viewModel.VerificationUri is { } uri && Uri.TryCreate(uri, UriKind.Absolute, out var target)
            && TopLevel.GetTopLevel(this)?.Launcher is { } launcher)
        {
            await launcher.LaunchUriAsync(target);
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}

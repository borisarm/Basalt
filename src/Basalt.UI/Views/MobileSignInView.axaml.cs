using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Basalt.Presentation.Sync;

namespace Basalt.UI.Views;

/// <summary>The device-flow page of the iOS navigation stack (F-015.1).</summary>
public partial class MobileSignInView : UserControl
{
    public MobileSignInView() => AvaloniaXamlLoader.Load(this);

    /// <summary>Raised when the user cancels; the parent pops the page.</summary>
    public event EventHandler? Cancelled;

    private GitHubSignInViewModel? ViewModel => DataContext as GitHubSignInViewModel;

    private async void OnOpenGitHub(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { UserCode: { } code } viewModel)
        {
            return;
        }

        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(code).ConfigureAwait(true);
        }

        if (viewModel.VerificationUri is { } uri && Uri.TryCreate(uri, UriKind.Absolute, out var target)
            && TopLevel.GetTopLevel(this)?.Launcher is { } launcher)
        {
            await launcher.LaunchUriAsync(target);
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Cancelled?.Invoke(this, EventArgs.Empty);
}

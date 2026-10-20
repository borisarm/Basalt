using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Basalt.Presentation.Common;
using Basalt.UI.Localization;

namespace Basalt.UI.Services;

/// <summary>A dialog window and the answer it produces when closed.</summary>
public sealed record DialogWindow<T>(Window Window, Task<T> Result);

/// <summary>
/// Builds the modal dialogs (in code, styled with the design tokens). Buttons are named (<c>Confirm</c>, <c>Cancel</c>,
/// <c>Option0</c>…) for keyboard access and tests.
/// </summary>
public static class DialogWindows
{
    public static DialogWindow<bool> Confirm(ConfirmRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = new TaskCompletionSource<bool>();
        var window = Frame(request.Title);
        var confirm = new Button { Name = "Confirm", Content = request.ConfirmText, IsDefault = true };
        if (request.IsDestructive)
        {
            confirm.Classes.Add("danger");
        }

        var cancel = new Button { Name = "Cancel", Content = request.CancelText ?? LocalizationSource.Instance["Common.Cancel"], IsCancel = true };
        confirm.Click += (_, _) => Close(window, result, true);
        cancel.Click += (_, _) => Close(window, result, false);
        window.Closed += (_, _) => result.TrySetResult(false);
        window.Content = Layout(new TextBlock { Text = request.Message, TextWrapping = Avalonia.Media.TextWrapping.Wrap }, cancel, confirm);
        return new DialogWindow<bool>(window, result.Task);
    }

    public static DialogWindow<string?> Prompt(PromptRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = new TaskCompletionSource<string?>();
        var window = Frame(request.Title);
        var input = new TextBox { Name = "Input", Text = request.InitialValue ?? string.Empty, PlaceholderText = request.Label };
        AutomationLabel(input, request.Label);
        var ok = new Button { Name = "Confirm", Content = LocalizationSource.Instance["Common.OK"], IsDefault = true };
        var cancel = new Button { Name = "Cancel", Content = LocalizationSource.Instance["Common.Cancel"], IsCancel = true };
        ok.Click += (_, _) => Close(window, result, input.Text);
        cancel.Click += (_, _) => Close<string?>(window, result, null);
        window.Closed += (_, _) => result.TrySetResult(null);
        window.Opened += (_, _) =>
        {
            input.Focus();
            input.SelectAll();
        };
        window.Content = Layout(new StackPanel { Spacing = 6, Children = { new TextBlock { Text = request.Label }, input } }, cancel, ok);
        return new DialogWindow<string?>(window, result.Task);
    }

    public static DialogWindow<int?> Choose(ChoiceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = new TaskCompletionSource<int?>();
        var window = Frame(request.Title);
        var buttons = request.Options.Select((option, index) =>
        {
            var button = new Button { Name = "Option" + index, Content = option, IsDefault = index == 0 };
            button.Click += (_, _) => Close<int?>(window, result, index);
            return (Control)button;
        }).ToList();
        var cancel = new Button { Name = "Cancel", Content = LocalizationSource.Instance["Common.Cancel"], IsCancel = true };
        cancel.Click += (_, _) => Close<int?>(window, result, null);
        window.Closed += (_, _) => result.TrySetResult(null);
        window.Content = Layout(new TextBlock { Text = request.Message, TextWrapping = Avalonia.Media.TextWrapping.Wrap }, [cancel, .. buttons]);
        return new DialogWindow<int?>(window, result.Task);
    }

    private static Window Frame(string title) => new()
    {
        Title = title,
        Width = 420,
        SizeToContent = SizeToContent.Height,
        CanResize = false,
        ShowInTaskbar = false,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
    };

    private static StackPanel Layout(Control body, params Control[] buttons)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        row.Children.AddRange(buttons);
        return new StackPanel { Margin = new Avalonia.Thickness(16), Spacing = 16, Children = { body, row } };
    }

    private static void AutomationLabel(Control control, string name) => Avalonia.Automation.AutomationProperties.SetName(control, name);

    private static void Close<T>(Window window, TaskCompletionSource<T> result, T value)
    {
        result.TrySetResult(value);
        window.Close();
    }
}

/// <summary><see cref="IDialogService"/> over modal Avalonia windows owned by the active window.</summary>
public sealed class DialogService : IDialogService
{
    public Task<bool> ConfirmAsync(ConfirmRequest request, CancellationToken ct) => ShowAsync(DialogWindows.Confirm(request));

    public Task<string?> PromptAsync(PromptRequest request, CancellationToken ct) => ShowAsync(DialogWindows.Prompt(request));

    public Task<int?> ChooseAsync(ChoiceRequest request, CancellationToken ct) => ShowAsync(DialogWindows.Choose(request));

    public async Task<string?> PickFolderAsync(string title, CancellationToken ct)
    {
        if (Owner() is not { } owner)
        {
            return null;
        }

        var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false }).ConfigureAwait(true);
        return folders.Count == 0 ? null : folders[0].TryGetLocalPath();
    }

    private static async Task<T> ShowAsync<T>(DialogWindow<T> dialog)
    {
        if (Owner() is { } owner)
        {
            await dialog.Window.ShowDialog(owner).ConfigureAwait(true);
        }
        else
        {
            dialog.Window.Show();
        }

        return await dialog.Result.ConfigureAwait(true);
    }

    private static Window? Owner() =>
        Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.Windows.FirstOrDefault(w => w.IsActive) ?? desktop.MainWindow
            : null;
}

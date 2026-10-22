using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Basalt.Presentation.Sync;

namespace Basalt.UI.Views;

/// <summary>Sync setup wizard (F-015.2, F-015.5): account, repository, dry-run preview and confirmation.</summary>
public partial class SyncSetupWindow : Window
{
    private readonly TaskCompletionSource<bool> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public SyncSetupWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public SyncSetupWindow(ConfigureSyncWizardViewModel viewModel)
        : this()
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ConfigureSyncWizardViewModel.IsConfigured) && viewModel.IsConfigured)
            {
                _result.TrySetResult(true);
                Close();
            }
        };
        Opened += async (_, _) => await viewModel.LoadAsync().ConfigureAwait(true);
        Closed += (_, _) => _result.TrySetResult(viewModel.IsConfigured);
    }

    public Task<bool> Result => _result.Task;

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}

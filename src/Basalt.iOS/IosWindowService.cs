using Basalt.Application.Vaults;
using Basalt.Domain.Vaults;
using Basalt.Presentation.Diff;
using Basalt.Presentation.Shell;

namespace Basalt.iOS;

/// <summary>
/// iOS has one window: navigation happens inside <c>MainView</c>, so the desktop window service cannot be used.
/// Settings, sign-in and diffs move into the navigation stack in T10.2.
/// </summary>
internal sealed class IosWindowService : IWindowService
{
    public void OpenVaultWindow(VaultId vault)
    {
    }

    public void OpenNewWindow()
    {
    }

    public void OpenSettings(VaultSessionInfo vault)
    {
    }

    public Task<bool> ShowSignInAsync(CancellationToken ct) => Task.FromResult(false);

    public Task ShowDiffAsync(DiffRequest request, CancellationToken ct) => Task.CompletedTask;

    public Task<bool> ShowConfigureSyncAsync(VaultSessionInfo vault, CancellationToken ct) => Task.FromResult(false);
}

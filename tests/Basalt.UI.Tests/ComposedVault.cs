using System.Globalization;
using Avalonia.Controls;
using Basalt.Application.Abstractions;
using Basalt.Application.Commands;
using Basalt.Application.Vaults;
using Basalt.Composition;
using Basalt.Presentation.Common;
using Basalt.Presentation.Localization;
using Basalt.Presentation.Shell;
using Basalt.Presentation.Workspace;
using Basalt.Testing;
using Basalt.Testing.Fakes;
using Basalt.UI.Localization;
using Basalt.UI.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Basalt.UI.Tests;

/// <summary>
/// A real vault on a temporary folder behind the real composition root (every use case and adapter), shown in a
/// headless <see cref="WorkspaceView"/>. Dialogs are the scriptable fake.
/// </summary>
internal sealed class ComposedVault : IAsyncDisposable
{
    private readonly TempDirectory _root;
    private readonly ServiceProvider _provider;
    private readonly IWindowService _windows;

    private ComposedVault(TempDirectory root, ServiceProvider provider, VaultSessionInfo vault, WorkspaceViewModel workspace, Window window, IWindowService windows)
    {
        _root = root;
        _provider = provider;
        _windows = windows;
        Vault = vault;
        Workspace = workspace;
        Window = window;
    }

    public VaultSessionInfo Vault { get; }

    public WorkspaceViewModel Workspace { get; }

    public Window Window { get; }

    public FakeDialogService Dialogs => (FakeDialogService)_provider.GetRequiredService<IDialogService>();

    public IWindowService Windows => _windows;

    public IServiceProvider Services => _provider;

    public string RootLocation => Vault.RootLocation;

    public static async Task<ComposedVault> CreateAsync(IReadOnlyDictionary<string, string> files, string culture = "en")
    {
        var root = TempDirectory.Create("ui-vault");
        var services = new ServiceCollection()
            .AddLogging(b => b.SetMinimumLevel(LogLevel.Warning))
            .AddBasaltCore(Path.Combine(root.Path, "appdata"))
            .AddBasaltPresentation(CommandPlatform.Windows, new ShellOptions(MultipleWindows: true));
        services.AddSingleton<IDialogService, FakeDialogService>();
        var windows = Substitute.For<IWindowService>();
        services.AddSingleton(windows);
        services.AddBasaltUi();
        var provider = services.BuildServiceProvider();
        LocalizationSource.Instance.Attach(new ResxLocalizer(CultureInfo.GetCultureInfo(culture)));
        var ct = TestContext.Current.CancellationToken;

        var created = await provider.GetRequiredService<ICommandHandler<CreateVault, VaultRegistration>>()
            .HandleAsync(new CreateVault("Notas", Path.Combine(root.Path, "vaults")), ct);
        foreach (var (path, text) in files)
        {
            var full = Path.Combine(created.Value.RootLocation, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await File.WriteAllTextAsync(full, text, ct);
        }

        var vault = (await provider.GetRequiredService<ICommandHandler<OpenVault, VaultSessionInfo>>().HandleAsync(new OpenVault(created.Value.Id), ct)).Value;
        var workspace = provider.GetRequiredService<WorkspaceFactory>().Create(vault);
        await workspace.InitializeAsync(ct);
        var window = new Window { Width = 1200, Height = 800, Content = new WorkspaceView { DataContext = workspace } };
        window.Show();
        return new ComposedVault(root, provider, vault, workspace, window, windows);
    }

    public async ValueTask DisposeAsync()
    {
        Window.Close();
        Workspace.Dispose();
        await _provider.GetRequiredService<ICommandHandler<CloseVault, Domain.Common.Unit>>().HandleAsync(new CloseVault(Vault.Id), CancellationToken.None);
        await _provider.DisposeAsync();
        await _root.DisposeAsync();
    }
}

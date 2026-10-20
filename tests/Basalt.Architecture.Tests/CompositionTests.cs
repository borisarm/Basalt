using Basalt.Application.Commands;
using Basalt.Application.Ports.Events;
using Basalt.Application.Ports.FileSystem;
using Basalt.Application.Ports.Index;
using Basalt.Application.Vaults;
using Basalt.Composition;
using Basalt.Domain.Vaults;
using Basalt.Presentation.Common;
using Basalt.Presentation.Settings;
using Basalt.Presentation.Shell;
using Basalt.Testing;
using Basalt.Testing.Contracts;
using Basalt.Testing.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Basalt.Architecture.Tests;

/// <summary>Spec 09 §8: the head's container builds with validation and resolves every use case and ViewModel; every port is covered.</summary>
public sealed class CompositionTests : IAsyncDisposable
{
    private readonly TempDirectory _appData = TempDirectory.Create("composition");
    private readonly ServiceProvider _provider;

    public CompositionTests()
    {
        var services = new ServiceCollection()
            .AddLogging(b => b.SetMinimumLevel(LogLevel.Warning))
            .AddBasaltCore(_appData.Path)
            .AddBasaltPresentation(CommandPlatform.Windows, new ShellOptions(MultipleWindows: true));

        // What the UI layer provides on a real head.
        services.AddSingleton<IUiDispatcher, ImmediateDispatcher>();
        services.AddSingleton<INotificationService, FakeNotificationService>();
        services.AddSingleton<IDialogService, FakeDialogService>();
        services.AddSingleton(Substitute.For<IWindowService>());
        services.AddSingleton(Substitute.For<IThemeService>());

        _provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _appData.DisposeAsync();
    }

    [Fact]
    [Trait("Spec", "TEST-8")]
    public void EveryUseCase_Resolves()
    {
        CompositionServiceCollectionExtensions.UseCases.Should().HaveCountGreaterThan(40);
        foreach (var (service, _) in CompositionServiceCollectionExtensions.UseCases)
        {
            _provider.GetService(service).Should().NotBeNull(service.Name);
        }
    }

    [Fact]
    [Trait("Spec", "TEST-8")]
    public void ShellWorkspaceAndSettings_Resolve()
    {
        var shells = _provider.GetRequiredService<ShellFactory>();
        using var shell = shells.Create();
        var vault = new VaultSessionInfo(new VaultId(Guid.NewGuid()), "Notas", _appData.Path, VaultSettings.Default, null);
        using var workspace = _provider.GetRequiredService<WorkspaceFactory>().Create(vault);
        using var settings = shells.CreateSettings(vault);

        shell.Content.Should().BeSameAs(shell.Picker);
        workspace.Panels.Should().NotBeNull();
        settings.Sections.Should().NotBeEmpty();
        _provider.GetRequiredService<ICommandRegistry>().Commands.Should().NotBeEmpty();
    }

    /// <summary>Ports whose implementations are created per vault session by a registered factory.</summary>
    private static readonly Type[] PerSession = [typeof(IVaultFileSystem), typeof(IFileWatcher), typeof(INoteIndex)];

    /// <summary>Ports that only aggregate others (their parts are checked through the composite).</summary>
    private static readonly Type[] Aggregated = [typeof(IFileReader), typeof(IFileWriter), typeof(IFileEnumerator), typeof(INoteIndexReader), typeof(INoteIndexWriter)];

    /// <summary>Pure ports tested with the real implementation through snapshots (spec 03 table).</summary>
    private static readonly Type[] PureWithSnapshots =
    [
        typeof(Application.Ports.Markdown.IMarkdownAnalyzer), typeof(Application.Ports.Markdown.IFrontmatterEditor), typeof(Application.Ports.Markdown.IMarkdownRenderer),
    ];

    /// <summary>Ports whose contract tests their factory's product.</summary>
    private static readonly Dictionary<Type, Type> ContractThroughProduct = new()
    {
        [typeof(IVaultFileSystemFactory)] = typeof(IVaultFileSystem),
        [typeof(INoteIndexFactory)] = typeof(INoteIndex),
    };

    /// <summary>Ports whose contract has another subject (the watcher is obtained from the file system under test).</summary>
    private static readonly Dictionary<Type, Type> NamedContracts = new()
    {
        [typeof(IFileWatcher)] = typeof(FileWatcherContract),
    };

    private static IEnumerable<Type> Ports() =>
        typeof(IEventBus).Assembly.GetTypes().Where(t => t.IsInterface && t.IsPublic && t.Namespace?.StartsWith("Basalt.Application.Ports", StringComparison.Ordinal) == true
            && t != typeof(IApplicationEvent) && t != typeof(IEventBus) && !Aggregated.Contains(t));

    [Fact]
    [Trait("Spec", "TEST-8")]
    public void EveryOutputPort_HasARegisteredImplementation()
    {
        foreach (var port in Ports().Where(p => !PerSession.Contains(p)))
        {
            var service = port.IsGenericTypeDefinition ? port.MakeGenericType(typeof(Application.Settings.AppSettings)) : port;
            _provider.GetService(service).Should().NotBeNull(port.Name);
        }
    }

    [Fact]
    [Trait("Spec", "TEST-8")]
    public void EveryOutputPort_HasAFakeAndAContract()
    {
        var testing = typeof(ContractTest<>).Assembly.GetTypes();
        foreach (var port in Ports().Where(p => !PureWithSnapshots.Contains(p)))
        {
            testing.Any(t => t.IsClass && !t.IsAbstract && t.Namespace == "Basalt.Testing.Fakes" && Implements(t, port)).Should().BeTrue($"{port.Name} needs a fake");
            if (NamedContracts.TryGetValue(port, out var named))
            {
                named.IsAbstract.Should().BeTrue();
                continue;
            }

            var contracted = ContractThroughProduct.GetValueOrDefault(port, port);
            testing.Any(t => t.IsAbstract && t.Namespace == "Basalt.Testing.Contracts" && Matches(ContractOf(t), contracted)).Should().BeTrue($"{port.Name} needs a contract");
        }
    }

    private static bool Implements(Type type, Type port) =>
        port.IsGenericTypeDefinition
            ? type.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == port)
            : port.IsAssignableFrom(type);

    private static Type? ContractOf(Type type)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(ContractTest<>))
            {
                return current.GetGenericArguments()[0];
            }
        }

        return null;
    }

    private static bool Matches(Type? sut, Type port) =>
        sut is not null && (sut == port || (port.IsGenericTypeDefinition && sut.IsGenericType && sut.GetGenericTypeDefinition() == port));
}

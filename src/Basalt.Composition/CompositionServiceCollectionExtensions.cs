using Basalt.Adapters.FileSystem;
using Basalt.Adapters.GitHub;
using Basalt.Adapters.Index;
using Basalt.Adapters.Markdown;
using Basalt.Adapters.Platform;
using Basalt.Application.Abstractions;
using Basalt.Application.Accounts;
using Basalt.Application.Bookmarks;
using Basalt.Application.Commands;
using Basalt.Application.Common;
using Basalt.Application.Events;
using Basalt.Application.Indexing;
using Basalt.Application.Notes;
using Basalt.Application.Ports.Events;
using Basalt.Application.Settings;
using Basalt.Application.Sync;
using Basalt.Application.Vaults;
using Basalt.Domain.Accounts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Basalt.Composition;

/// <summary>
/// DI registration of the core (spec 01 §2): application services, every use case (commands decorated with logging,
/// validation and the per-vault lock) and the portable adapters. Heads add the platform adapters, logging and the UI.
/// </summary>
public static class CompositionServiceCollectionExtensions
{
    /// <param name="appDataOverride">Alternative AppData folder (tests, portable installs).</param>
    public static IServiceCollection AddBasaltCore(this IServiceCollection services, string? appDataOverride = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddBasaltFileSystemAdapter();
        services.AddBasaltMarkdownAdapter();
        services.AddBasaltIndexAdapter();
        services.AddBasaltPlatformAdapter(appDataOverride);
        services.AddBasaltGitHubAdapter(GitHubHost.GitHubDotCom);

        services.TryAddSingleton<IEventBus, InMemoryEventBus>();
        services.TryAddSingleton<VaultSessions>();
        services.TryAddSingleton<IVaultSessions>(sp => sp.GetRequiredService<VaultSessions>());
        services.TryAddSingleton<VaultLocks>();
        services.TryAddSingleton<SyncLocks>();
        services.TryAddSingleton<SyncCoordinator>();
        services.TryAddSingleton<IVaultSyncRunnerFactory, VaultSyncRunnerFactory>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IVaultSessionInitializer, SyncSessionInitializer>());
        services.TryAddSingleton<IVaultRegistry, VaultRegistry>();
        services.TryAddSingleton<IAccountStore, AccountStore>();
        services.TryAddSingleton<IAppSettingsStore, AppSettingsStore>();
        services.TryAddTransient<EntryMover>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IVaultSessionInitializer, IndexingSessionInitializer>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IVaultSessionInitializer, ProductivitySessionInitializer>());

        AddUseCases(services);

        services.TryAddEnumerable(ServiceDescriptor.Transient<ICommandContribution, CoreCommands>());
        services.TryAddSingleton<ICommandRegistry, CommandRegistry>();
        return services;
    }

    /// <summary>Every concrete use case of <c>Basalt.Application</c>, by its handler interfaces.</summary>
    public static IReadOnlyList<(Type Service, Type Implementation)> UseCases { get; } =
    [
        .. typeof(CreateNoteHandler).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false } && t.Namespace?.StartsWith("Basalt.Application", StringComparison.Ordinal) == true)
            .SelectMany(t => t.GetInterfaces()
                .Where(i => i.IsGenericType && (i.GetGenericTypeDefinition() == typeof(ICommandHandler<,>) || i.GetGenericTypeDefinition() == typeof(IQueryHandler<,>)))
                .Where(_ => !IsDecorator(t))
                .Select(i => (i, t))),
    ];

    private static bool IsDecorator(Type type) => type.IsGenericType;

    private static void AddUseCases(IServiceCollection services)
    {
        foreach (var (service, implementation) in UseCases)
        {
            services.TryAddTransient(implementation);
            if (service.GetGenericTypeDefinition() == typeof(IQueryHandler<,>))
            {
                services.AddTransient(service, sp => sp.GetRequiredService(implementation));
                continue;
            }

            var arguments = service.GetGenericArguments();
            services.AddTransient(service, sp =>
            {
                var inner = sp.GetRequiredService(implementation);
                var locked = ActivatorUtilities.CreateInstance(sp, typeof(VaultLockDecorator<,>).MakeGenericType(arguments), inner);
                var validated = ActivatorUtilities.CreateInstance(sp, typeof(ValidationHandlerDecorator<,>).MakeGenericType(arguments), locked);
                return ActivatorUtilities.CreateInstance(sp, typeof(LoggingHandlerDecorator<,>).MakeGenericType(arguments), validated);
            });
        }
    }
}

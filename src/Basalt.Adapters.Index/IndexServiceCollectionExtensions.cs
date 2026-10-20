using Basalt.Application.Ports.Index;
using Microsoft.Extensions.DependencyInjection;

namespace Basalt.Adapters.Index;

/// <summary>DI registration of the SQLite index adapter.</summary>
public static class IndexServiceCollectionExtensions
{
    public static IServiceCollection AddBasaltIndexAdapter(this IServiceCollection services)
    {
        services.AddSingleton<INoteIndexFactory, SqliteNoteIndexFactory>();
        return services;
    }
}

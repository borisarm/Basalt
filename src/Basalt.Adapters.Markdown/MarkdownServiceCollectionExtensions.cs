using Basalt.Application.Ports.Markdown;
using Microsoft.Extensions.DependencyInjection;

namespace Basalt.Adapters.Markdown;

/// <summary>DI registration of the Markdown adapter (Markdig + YamlDotNet).</summary>
public static class MarkdownServiceCollectionExtensions
{
    public static IServiceCollection AddBasaltMarkdownAdapter(this IServiceCollection services)
    {
        services.AddSingleton<IMarkdownAnalyzer, MarkdigAnalyzer>();
        services.AddSingleton<IFrontmatterEditor, YamlFrontmatterEditor>();
        return services;
    }
}

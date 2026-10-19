using System.Globalization;
using System.Text.RegularExpressions;

namespace Basalt.Domain.Templates;

/// <summary>Values available to templates.</summary>
/// <param name="Now">Local date and time used by {{date}} and {{time}}.</param>
public sealed record TemplateContext(string Title, DateTime Now, CultureInfo Culture);

/// <summary>
/// Renders Obsidian core-template variables (spec 02 §9): <c>{{title}}</c>, <c>{{date}}</c>, <c>{{time}}</c>,
/// <c>{{date:FORMAT}}</c> and <c>{{time:FORMAT}}</c>. Unknown variables are left untouched.
/// </summary>
public static partial class TemplateRenderer
{
    public const string DefaultDateFormat = "YYYY-MM-DD";
    public const string DefaultTimeFormat = "HH:mm";

    public static string Render(string template, TemplateContext context)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(context);
        return VariableRegex().Replace(template, match =>
        {
            var name = match.Groups["name"].Value.ToLowerInvariant();
            var format = match.Groups["format"].Success ? match.Groups["format"].Value : null;
            return name switch
            {
                "title" => context.Title,
                "date" => MomentFormat.Format(context.Now, format ?? DefaultDateFormat, context.Culture),
                _ => MomentFormat.Format(context.Now, format ?? DefaultTimeFormat, context.Culture),
            };
        });
    }

    [GeneratedRegex(@"\{\{\s*(?<name>title|date|time)(?::(?<format>[^}]*))?\s*\}\}", RegexOptions.IgnoreCase)]
    private static partial Regex VariableRegex();
}

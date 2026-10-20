using System.Globalization;
using System.Resources;
using System.Text.RegularExpressions;
using Basalt.Domain.Common;

namespace Basalt.Presentation.Localization;

/// <summary>UI texts (spec 07 §1): keys in <c>Resources/Strings.resx</c> (en) and <c>Strings.es.resx</c>.</summary>
public interface ILocalizer
{
    CultureInfo Culture { get; set; }

    event EventHandler? CultureChanged;

    /// <summary>The text for <paramref name="key"/>; a missing key shows as <c>[key]</c> instead of failing.</summary>
    string Get(string key);

    /// <summary><see cref="Get"/> formatted with <see cref="Culture"/>.</summary>
    string Format(string key, params object?[] arguments);

    /// <summary>
    /// The message of <c>Errors.{code}</c> with <c>{name}</c> placeholders filled from the error's metadata; unknown codes
    /// fall back to the error's own message.
    /// </summary>
    string Error(Error error);
}

public sealed partial class ResxLocalizer(CultureInfo culture) : ILocalizer
{
    public static ResourceManager Resources { get; } = new("Basalt.Presentation.Resources.Strings", typeof(ResxLocalizer).Assembly);

    private CultureInfo _culture = culture;

    public event EventHandler? CultureChanged;

    public CultureInfo Culture
    {
        get => _culture;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (!Equals(value, _culture))
            {
                _culture = value;
                CultureChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public string Get(string key) => Resources.GetString(key, _culture) ?? $"[{key}]";

    public string Format(string key, params object?[] arguments) => string.Format(_culture, Get(key), arguments);

    public string Error(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (Resources.GetString("Errors." + error.Code, _culture) is not { } template)
        {
            return error.Message;
        }

        return Placeholder().Replace(template, match =>
            error.Metadata?.GetValueOrDefault(match.Groups[1].Value) is { } value ? Convert.ToString(value, _culture) ?? string.Empty : "…");
    }

    [GeneratedRegex(@"\{([A-Za-z][A-Za-z0-9_]*)\}")]
    private static partial Regex Placeholder();
}

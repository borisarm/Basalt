using System.Globalization;
using System.Text;

namespace Basalt.Domain.Templates;

/// <summary>
/// Moment.js-style date formats used by Obsidian templates and daily notes (spec 02 §9): YYYY, YY, Q, MMMM, MMM, MM, M,
/// Do, DD, D, dddd, ddd, HH, H, hh, h, mm, m, ss, s, A, a and <c>[literal]</c>.
/// </summary>
public static class MomentFormat
{
    // Longest tokens first so that "MMMM" wins over "MM".
    private static readonly string[] Tokens =
        ["YYYY", "YY", "MMMM", "MMM", "MM", "M", "Do", "DD", "D", "dddd", "ddd", "HH", "H", "hh", "h", "mm", "m", "ss", "s", "Q", "A", "a"];

    private static readonly Dictionary<string, string> DotNetEquivalents = new(StringComparer.Ordinal)
    {
        ["YYYY"] = "yyyy",
        ["YY"] = "yy",
        ["MMMM"] = "MMMM",
        ["MMM"] = "MMM",
        ["MM"] = "MM",
        ["M"] = "M",
        ["Do"] = "d",
        ["DD"] = "dd",
        ["D"] = "d",
        ["dddd"] = "dddd",
        ["ddd"] = "ddd",
        ["HH"] = "HH",
        ["H"] = "H",
        ["hh"] = "hh",
        ["h"] = "h",
        ["mm"] = "mm",
        ["m"] = "m",
        ["ss"] = "ss",
        ["s"] = "s",
        ["Q"] = "M",
        ["A"] = "tt",
        ["a"] = "tt",
    };

    public static string Format(DateTime value, string format, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(culture);
        var builder = new StringBuilder();
        foreach (var (isToken, text) in Tokenize(format))
        {
            builder.Append(isToken ? FormatToken(value, text, culture) : text);
        }

        return builder.ToString();
    }

    /// <summary>Translates to a .NET custom format string (Do and Q have no exact .NET equivalent).</summary>
    public static string ToDotNet(string format)
    {
        ArgumentNullException.ThrowIfNull(format);
        var builder = new StringBuilder();
        foreach (var (isToken, text) in Tokenize(format))
        {
            builder.Append(isToken ? DotNetEquivalents[text] : $"'{text.Replace("'", "\\'", StringComparison.Ordinal)}'");
        }

        // A single-character custom format would be read as a standard format by .NET.
        return builder.Length == 1 ? "%" + builder : builder.ToString();
    }

    private static IEnumerable<(bool IsToken, string Text)> Tokenize(string format)
    {
        var literal = new StringBuilder();
        var i = 0;
        while (i < format.Length)
        {
            if (format[i] == '[')
            {
                if (literal.Length > 0)
                {
                    yield return (false, literal.ToString());
                    literal.Clear();
                }

                var close = format.IndexOf(']', i + 1);
                var end = close < 0 ? format.Length : close;
                yield return (false, format[(i + 1)..end]);
                i = close < 0 ? format.Length : close + 1;
                continue;
            }

            var token = Tokens.FirstOrDefault(t => string.CompareOrdinal(format, i, t, 0, t.Length) == 0);
            if (token is null)
            {
                literal.Append(format[i]);
                i++;
                continue;
            }

            if (literal.Length > 0)
            {
                yield return (false, literal.ToString());
                literal.Clear();
            }

            yield return (true, token);
            i += token.Length;
        }

        if (literal.Length > 0)
        {
            yield return (false, literal.ToString());
        }
    }

    private static string FormatToken(DateTime value, string token, CultureInfo culture)
    {
        var dtf = culture.DateTimeFormat;
        var hour12 = value.Hour % 12 == 0 ? 12 : value.Hour % 12;
        return token switch
        {
            "YYYY" => value.Year.ToString("D4", culture),
            "YY" => (value.Year % 100).ToString("D2", culture),
            "Q" => (((value.Month - 1) / 3) + 1).ToString(culture),
            "MMMM" => dtf.GetMonthName(value.Month),
            "MMM" => dtf.GetAbbreviatedMonthName(value.Month),
            "MM" => value.Month.ToString("D2", culture),
            "M" => value.Month.ToString(culture),
            "Do" => Ordinal(value.Day, culture),
            "DD" => value.Day.ToString("D2", culture),
            "D" => value.Day.ToString(culture),
            "dddd" => dtf.GetDayName(value.DayOfWeek),
            "ddd" => dtf.GetAbbreviatedDayName(value.DayOfWeek),
            "HH" => value.Hour.ToString("D2", culture),
            "H" => value.Hour.ToString(culture),
            "hh" => hour12.ToString("D2", culture),
            "h" => hour12.ToString(culture),
            "mm" => value.Minute.ToString("D2", culture),
            "m" => value.Minute.ToString(culture),
            "ss" => value.Second.ToString("D2", culture),
            "s" => value.Second.ToString(culture),
            "A" => value.Hour < 12 ? Designator(dtf.AMDesignator, "AM") : Designator(dtf.PMDesignator, "PM"),
            _ => (value.Hour < 12 ? Designator(dtf.AMDesignator, "AM") : Designator(dtf.PMDesignator, "PM")).ToLowerInvariant(),
        };
    }

    private static string Designator(string cultureValue, string fallback) => cultureValue.Length == 0 ? fallback : cultureValue;

    private static string Ordinal(int day, CultureInfo culture)
    {
        switch (culture.TwoLetterISOLanguageName)
        {
            case "en":
                var suffix = (day % 100) is >= 11 and <= 13 ? "th" : (day % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };
                return day.ToString(culture) + suffix;
            case "es" or "it" or "pt":
                return day.ToString(culture) + "º";
            default:
                return day.ToString(culture) + ".";
        }
    }
}

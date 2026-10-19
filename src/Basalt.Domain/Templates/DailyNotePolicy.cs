using System.Globalization;
using Basalt.Domain.Common;
using Basalt.Domain.Notes;

namespace Basalt.Domain.Templates;

/// <summary>Location of the daily note of a date (spec 02 §9): <c>{folder}/{format(date)}.md</c>.</summary>
public static class DailyNotePolicy
{
    /// <param name="folder">Daily notes folder ("" = vault root).</param>
    /// <param name="format">Moment format; '/' creates sub-folders (e.g. "YYYY/YYYY-MM-DD").</param>
    /// <returns><c>daily_note.invalid_format</c> for an empty format, or the <see cref="VaultPath"/> errors when the
    /// formatted name is not a valid path.</returns>
    public static Result<VaultPath> PathFor(DateOnly date, string folder, string format, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(culture);
        if (format.Trim().Length == 0)
        {
            return new Error(DomainErrors.DailyNoteInvalidFormat, "The daily note date format is empty.");
        }

        var name = MomentFormat.Format(date.ToDateTime(TimeOnly.MinValue), format, culture) + ".md";
        return VaultPath.Create(folder.Length == 0 ? name : $"{folder}/{name}");
    }
}

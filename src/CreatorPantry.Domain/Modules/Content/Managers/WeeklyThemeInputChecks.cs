using System.Text.RegularExpressions;

namespace CreatorPantry.Domain.Modules.Content.Managers;

/// <summary>
/// The shape rules for a week replace, returned as <c>(Field, Message)</c> pairs so the validator and any
/// later caller cannot drift apart.
/// </summary>
internal static partial class WeeklyThemeInputChecks
{
    [GeneratedRegex("^[a-z0-9][a-z0-9-]*$")]
    private static partial Regex KeyPattern();

    /// <summary>Trimmed, and null when nothing is left. The form every text field is stored in.</summary>
    public static string? Normalize(string? value)
    {
        var trimmed = value?.Trim();

        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    /// <summary>Whether <paramref name="key"/> is shaped like a theme key. Says nothing about whether one exists.</summary>
    public static bool IsKey(string key) => key.Length <= WeeklyThemePolicy.KeyMaxLength && KeyPattern().IsMatch(key);

    public static IEnumerable<(string, string)> Themes(IReadOnlyList<WeeklyThemeInput?>? themes)
    {
        const string field = nameof(ReplaceWeeklyThemesViewModel.Themes);

        if (themes is null)
        {
            // Not an error: a body with no list is the empty week, which is how a creator clears every day.
            yield break;
        }

        if (themes.Count > WeeklyThemePolicy.MaxLiveThemes)
        {
            yield return (field, $"A week has {WeeklyThemePolicy.MaxLiveThemes} days, so at most {WeeklyThemePolicy.MaxLiveThemes} themes.");
            yield break;
        }

        var days = new HashSet<DayOfWeek>();
        var keys = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < themes.Count; index++)
        {
            var theme = themes[index];

            if (theme?.Day is not { } day || !Enum.IsDefined(day))
            {
                yield return ($"{field}[{index}].Day", "Name a day of the week, such as Monday.");
            }
            else if (!days.Add(day))
            {
                yield return ($"{field}[{index}].Day", "A day holds one theme.");
            }

            var key = Normalize(theme?.Key);
            if (key is null || !IsKey(key))
            {
                yield return ($"{field}[{index}].Key", "Use a lowercase key such as meat-free-monday.");
            }
            else if (!keys.Add(key))
            {
                yield return ($"{field}[{index}].Key", "Each theme can be listed once.");
            }

            var displayName = Normalize(theme?.DisplayName);
            if (displayName is null)
            {
                yield return ($"{field}[{index}].DisplayName", "Give the theme a name.");
            }
            else if (displayName.Length > WeeklyThemePolicy.DisplayNameMaxLength)
            {
                yield return ($"{field}[{index}].DisplayName",
                    $"A name can be at most {WeeklyThemePolicy.DisplayNameMaxLength} characters.");
            }

            if (Normalize(theme?.Description) is { } description
                && description.Length > WeeklyThemePolicy.DescriptionMaxLength)
            {
                yield return ($"{field}[{index}].Description",
                    $"A description can be at most {WeeklyThemePolicy.DescriptionMaxLength} characters.");
            }
        }
    }
}

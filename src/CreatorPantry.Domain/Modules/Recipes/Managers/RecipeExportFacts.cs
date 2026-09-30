using System.Globalization;

namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// The header facts every export formats the same way, so the Markdown and PDF outputs cannot drift apart on
/// what a duration or a yield reads as.
/// </summary>
internal static class RecipeExportFacts
{
    /// <summary>The recorded prep, cook and total times, in that order; zero or negative values are not times.</summary>
    public static IReadOnlyList<(string Label, string Text)> Times(RecipeSnapshotHeader header)
    {
        var times = new List<(string, string)>();
        Add(times, "Prep", header.PrepTimeMinutes);
        Add(times, "Cook", header.CookTimeMinutes);
        Add(times, "Total", header.TotalTimeMinutes);
        return times;

        static void Add(List<(string, string)> list, string label, int? minutes)
        {
            if (minutes is not > 0) return;
            var hours = minutes.Value / 60;
            var rest = minutes.Value % 60;
            list.Add((label, (hours, rest) switch
            {
                (0, _) => $"{rest} min",
                (_, 0) => $"{hours} h",
                _ => $"{hours} h {rest} min",
            }));
        }
    }

    /// <summary>
    /// The creator's own yield wording when there is any; otherwise only the serving count. Never composed
    /// from the measured batch, which could contradict what the creator wrote.
    /// </summary>
    public static string? Yield(RecipeSnapshotHeader header)
    {
        if (!string.IsNullOrWhiteSpace(header.YieldText)) return header.YieldText.Trim();
        if (header.ServingCount is not > 0) return null;
        return header.ServingCount == 1
            ? "1 serving"
            : header.ServingCount.Value.ToString("0.##", CultureInfo.InvariantCulture) + " servings";
    }
}

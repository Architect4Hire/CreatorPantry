using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Modules.Measurement.Managers;

namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>Everything <see cref="RecipeMarkdownExporter"/> may read. The exporter does no I/O.</summary>
public sealed record RecipeMarkdownInput
{
    /// <summary>The exact pinned version being exported.</summary>
    public required RecipeSnapshotDocument Snapshot { get; init; }

    /// <summary>The version's own number, for the export footer.</summary>
    public required int VersionNumber { get; init; }

    public RecipeVersionReadiness Readiness { get; init; }

    public RecipeExportTemplate Template { get; init; }

    public RecipeUnitPresentation UnitPresentation { get; init; }

    /// <summary>The units the snapshot's lines refer to, by id. Only needed for a converted presentation.</summary>
    public IReadOnlyDictionary<Guid, MeasurementUnitServiceModel> UnitsById { get; init; } =
        new Dictionary<Guid, MeasurementUnitServiceModel>();

    /// <summary>
    /// The unit to present each dimension in for <see cref="UnitPresentation"/>. A target whose system does not
    /// match the presentation is ignored, so a caller cannot produce a "metric" export in cups.
    /// </summary>
    public IReadOnlyDictionary<MeasurementDimension, MeasurementUnitServiceModel> TargetUnits { get; init; } =
        new Dictionary<MeasurementDimension, MeasurementUnitServiceModel>();

    /// <summary>The creator-accepted editorial revision for this version, or <c>null</c>.</summary>
    public RecipeExportEditorial? Editorial { get; init; }
}

public sealed record RecipeMarkdownIssue(string Code, string Message);

/// <summary>
/// The outcome of an export. <see cref="Markdown"/> is present only when nothing required is missing.
/// </summary>
public sealed record RecipeMarkdownReport(
    string? Markdown,
    IReadOnlyList<RecipeMarkdownIssue> MissingRequired,
    IReadOnlyList<RecipeMarkdownIssue> Warnings)
{
    public bool IsComplete => Markdown is not null && MissingRequired.Count == 0;
}

/// <summary>Stable codes for <see cref="RecipeMarkdownIssue"/>.</summary>
public static class RecipeMarkdownCodes
{
    public const string RecipeNotApproved = "recipe_markdown_recipe_not_approved";
    public const string NameMissing = "recipe_markdown_name_missing";
    public const string IngredientsMissing = "recipe_markdown_ingredients_missing";
    public const string InstructionsMissing = "recipe_markdown_instructions_missing";
    public const string UnitNotConverted = "recipe_markdown_unit_not_converted";
    public const string EditorialNotCurrent = "recipe_markdown_editorial_not_current";
    public const string SubstitutionUnmatched = "recipe_markdown_substitution_unmatched";
    public const string TimesMissing = "recipe_markdown_times_missing";
    public const string YieldMissing = "recipe_markdown_yield_missing";
}

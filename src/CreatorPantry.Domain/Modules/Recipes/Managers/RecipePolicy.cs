namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// Limits and invariants for the recipe aggregate, shared by EF configuration and by the validation that
/// arrives with the write seam. Recipes are workspace-owned creator intellectual property (tenancy.md):
/// every entity in the aggregate carries a <c>WorkspaceId</c> and is covered by the global query filter.
/// </summary>
/// <remarks>
/// The lengths below bound storage, not expression. They are generous on the fields that hold the creator's
/// own words — an ingredient line, a step, a headnote — because recipes.md makes that text canonical and
/// truncating it would destroy the thing the model exists to preserve.
/// </remarks>
public static class RecipePolicy
{
    /// <summary>
    /// The version number every recipe's history starts at. Numbering runs from 1 rather than 0 because it
    /// is a number creators cite, and "version 0" reads as "no version".
    /// </summary>
    public const int FirstVersionNumber = 1;

    /// <summary>
    /// The largest value any of the four time fields may carry: one year, in minutes.
    /// </summary>
    /// <remarks>
    /// Generous on purpose — curing, fermenting and ageing are real recipe steps measured in months — while
    /// still catching the common paste error of a millisecond or second value into a field that means
    /// minutes. Shared by the create and update validators so the two cannot come to disagree about what a
    /// plausible time is.
    /// </remarks>
    public const int MaxTimeMinutes = 525_600;

    public const int TitleMaxLength = 200;

    /// <summary>A one-paragraph summary, not the recipe's prose introduction.</summary>
    public const int DescriptionMaxLength = 2000;

    /// <summary>
    /// The editorial introduction that precedes a recipe, and the creator's free-form notes and storage
    /// guidance. Generous because these are essays in miniature and a headnote routinely runs several
    /// paragraphs.
    /// </summary>
    /// <remarks>
    /// Capped at 4000 rather than higher on purpose: that is the largest <c>nvarchar(n)</c> SQL Server will
    /// take, and anything above it becomes <c>nvarchar(max)</c>, where the limit stops being a column
    /// constraint and becomes a comment. The article-length prose that would need more is a content draft,
    /// which is a different entity with a different lifecycle — not a recipe field.
    /// </remarks>
    public const int LongTextMaxLength = 4000;

    /// <summary>"Adapted from my grandmother's card", in the creator's own words.</summary>
    public const int AttributionMaxLength = 500;

    public const int UrlMaxLength = 2048;

    /// <summary>The creator's entered yield phrasing: "makes 12 muffins", "serves 4 to 6".</summary>
    public const int YieldTextMaxLength = 200;

    /// <summary>An ingredient-group or instruction-group heading: "For the streusel".</summary>
    public const int GroupTitleMaxLength = 200;

    /// <summary>
    /// One ingredient line or one equipment line exactly as the creator typed it. This is the canonical
    /// wording (recipes.md); every normalized reference beside it is additive.
    /// </summary>
    public const int LineTextMaxLength = 500;

    /// <summary>One instruction step. Steps are prose and occasionally long ones.</summary>
    public const int StepTextMaxLength = 4000;

    /// <summary>A per-line preparation note ("finely chopped") or a per-step aside.</summary>
    public const int NoteMaxLength = 1000;

    /// <summary>A per-usage caption on a linked media asset. Not alt text, which lives on the asset.</summary>
    public const int CaptionMaxLength = 500;

    /// <summary>
    /// The parsed ingredient-name span of a line, kept so an interface can emphasise it without re-parsing.
    /// Bounded by the vocabulary's own name length because that is the longest thing it can ever hold.
    /// </summary>
    public const int IngredientNameTextMaxLength = 128;
}

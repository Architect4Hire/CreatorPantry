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
    /// The editorial states a library search returns when the caller asked for none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every state except <see cref="RecipeStatus.Archived"/>, and <strong>derived rather than listed</strong>:
    /// a state added to <see cref="RecipeStatus"/> tomorrow appears in the default library automatically,
    /// which is the safe direction. A hand-written list would quietly hide it, and nobody would notice until
    /// a creator asked where their recipes had gone.
    /// </para>
    /// <para>
    /// Archiving is how a creator says "not now" about a recipe they are not finished with. A default that
    /// kept showing it would make the command pointless; excluding it permanently would lose their work. So
    /// the exclusion lives in the <em>default</em> only — <c>?status=Archived</c> lists them, and REC-006 asks
    /// for exactly that shape.
    /// </para>
    /// <para>
    /// Applied by <c>RecipeSearchQueryFactory</c>, where a request becomes criteria, and deliberately not by
    /// the repository: "an empty status list is the absence of a filter" is a persistence contract that other
    /// callers depend on, and turning absence into a default is a product decision.
    /// </para>
    /// </remarks>
    public static readonly IReadOnlyList<RecipeStatus> DefaultSearchStatuses =
        [.. Enum.GetValues<RecipeStatus>().Where(status => status != RecipeStatus.Archived)];

    /// <summary>
    /// Whether a recipe in this state accepts changes to its content.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The single home of REC-006's freeze.</strong> An archived recipe keeps everything — its
    /// versions, its tags, its media links, its history — and accepts nothing new until it is brought back.
    /// Every seam that writes recipe content is required to ask this first: the edit seam and the version
    /// restore do today, and the AI proposal, publishing and test-run seams must when they arrive. Asking
    /// here rather than each re-deciding is what keeps the answer one answer.
    /// </para>
    /// <para>
    /// <strong>Duplicating is not a change and is not gated by this.</strong> A copy reads the archived
    /// recipe and writes a different one, which is the ordinary way to take shelved work somewhere new —
    /// refusing it would make the archive a place content goes to become unreachable.
    /// </para>
    /// </remarks>
    public static bool AcceptsContentChanges(RecipeStatus status) => status != RecipeStatus.Archived;

    /// <summary>
    /// The state a recipe returns to when it comes back from the archive.
    /// </summary>
    /// <remarks>
    /// <see cref="RecipeStatus.Draft"/>, and not the state it held before, because nothing records what that
    /// was — storing it would be a column whose only reader is this one line. Draft is also the honest
    /// answer: <see cref="RecipeStatus.Ready"/> is a claim the creator makes about a recipe they consider
    /// finished, and one coming back off the shelf is being picked up again. Saying it is ready is theirs to
    /// do, in one more edit.
    /// </remarks>
    public const RecipeStatus UnarchivedStatus = RecipeStatus.Draft;

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

    /// <summary>
    /// How many instruction groups one recipe may carry. A cap rather than a judgement about method length:
    /// without one, a single request can stage unbounded rows, and an import can turn one recipe into
    /// thousands of them. Generous — a multi-day recipe with many phases is a real recipe.
    /// </summary>
    public const int MaxInstructionGroupsPerRecipe = 50;

    /// <summary>
    /// How many instruction steps one recipe may carry in total, across every group. Bounds the same risk as
    /// <see cref="MaxInstructionGroupsPerRecipe"/>, at the level that actually determines payload size.
    /// </summary>
    public const int MaxInstructionStepsPerRecipe = 200;
}

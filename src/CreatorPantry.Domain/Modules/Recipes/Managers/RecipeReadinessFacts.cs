using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.Ingredients.Managers;

namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// One of a recipe's ingredient lines, as far as readiness cares about it.
/// </summary>
/// <remarks>
/// Carries the creator's display text so a finding can name the line the way the creator wrote it, and the
/// vocabulary reference so the allergen question can be asked about it. Nothing else of the line is read — a
/// readiness evaluation has no use for quantities.
/// </remarks>
/// <param name="IngredientId">
/// The vocabulary entry this line resolved to, or <c>null</c> when it resolved to none. Null and
/// <see cref="IngredientMatchStatus.Matched"/> cannot both hold: a check constraint already forbids it.
/// </param>
public sealed record RecipeReadinessIngredientLine(
    Guid Id,
    string DisplayText,
    IngredientMatchStatus MatchStatus,
    Guid? IngredientId);

/// <summary>
/// One unresolved issue from a test of the version being evaluated.
/// </summary>
/// <param name="Title">The problem in the tester's own words, so a finding can quote it rather than count it.</param>
public sealed record RecipeReadinessOpenIssue(
    Guid Id,
    Guid RecipeTestRunId,
    TestIssueSeverity Severity,
    string Title);

/// <summary>
/// Everything about a recipe that the readiness rules read from this module's own tables.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Gathered in one read, evaluated with no further I/O.</strong> The evaluator is a pure function over
/// this record plus <see cref="RecipeReadinessExternalFacts"/>, which is what makes every rule testable without a
/// database and what makes the result reproducible: the same facts always produce the same findings.
/// </para>
/// <para>
/// <strong>The evaluation is of the recipe as it now stands, and the version it names is the one that content
/// belongs to.</strong> Nothing here is read from a version snapshot: the live rows <em>are</em> the latest
/// version's content, and parsing the archived document instead would answer the same question more slowly and
/// from a different shape. <see cref="EvaluatedVersionId"/> and <see cref="EvaluatedVersionNumber"/> record which
/// version that is, so a result can be cited later, and <see cref="RecipeRowVersion"/> pins the exact state —
/// which is what lets 10.6 require that an approval quote an evaluation of the recipe it is approving rather than
/// of an earlier one.
/// </para>
/// <para>
/// <strong>No <c>WorkspaceId</c>.</strong> The facts were read inside a resolved workspace through the global
/// query filter; carrying the id into the evaluator would give a pure function a tenancy concern it cannot
/// enforce (tenancy.md).
/// </para>
/// </remarks>
/// <param name="EvaluatedVersionNumber">
/// The number of the recipe's latest version, or <c>null</c> for a recipe with no version at all. Null is
/// reachable in principle only — creation writes version 1 in the same transaction — and is modelled honestly
/// rather than defaulted to zero, which would read as a version.
/// </param>
/// <param name="HasHeroAsset">
/// Whether a <c>RecipeAssetLink</c> with <see cref="RecipeAssetRole.Hero"/> exists. Whether the asset behind it
/// exists is a question nothing can answer yet; see <see cref="RecipeReadinessCatalogue"/>.
/// </param>
/// <param name="TestedCurrentVersion">Whether any test run names <see cref="EvaluatedVersionId"/>.</param>
/// <param name="LatestTestOutcome">
/// The verdict of the most recently <em>cooked</em> test of that version, or <c>null</c> when there is none.
/// Ordered by when the cooking happened, not by when the notes were typed, for the reason the test history is.
/// </param>
public sealed record RecipeReadinessFacts
{
    public required Guid RecipeId { get; init; }

    public required byte[] RecipeRowVersion { get; init; }

    public required Guid? EvaluatedVersionId { get; init; }

    public required int? EvaluatedVersionNumber { get; init; }

    public required string Title { get; init; }

    public required string? Description { get; init; }

    public required string? AttributionText { get; init; }

    public required string? SourceUrl { get; init; }

    public required Guid? CuisineId { get; init; }

    public required Guid? CourseId { get; init; }

    public required int TagCount { get; init; }

    public required int? PrepTimeMinutes { get; init; }

    public required int? CookTimeMinutes { get; init; }

    public required int? RestTimeMinutes { get; init; }

    public required int? TotalTimeMinutes { get; init; }

    public required string? YieldText { get; init; }

    public required decimal? YieldQuantity { get; init; }

    public required Guid? YieldUnitId { get; init; }

    public required decimal? ServingCount { get; init; }

    public required int InstructionStepCount { get; init; }

    public required bool HasHeroAsset { get; init; }

    public required Guid? HeroAssetLinkId { get; init; }

    public required bool TestedCurrentVersion { get; init; }

    public required TestRunOutcome? LatestTestOutcome { get; init; }

    public required Guid? LatestTestRunId { get; init; }

    public required IReadOnlyList<RecipeReadinessIngredientLine> IngredientLines { get; init; }

    public required IReadOnlyList<RecipeReadinessOpenIssue> OpenIssues { get; init; }
}

/// <summary>
/// The facts the readiness rules need from other modules, gathered by the facade because Business may not
/// cross a module boundary.
/// </summary>
/// <remarks>
/// <para>
/// The same structural split the test history's tester names live under, in the other direction: there the
/// facade enriched a result afterwards, here it supplies inputs beforehand — because the rules cannot run
/// without them. <c>RecipeFacade.PrepareCreateAsync</c> already gathers cross-module facts before calling
/// Business, and this follows it.
/// </para>
/// <para>
/// <strong>Both members are honest about being unavailable.</strong> They are supplied by reads that can be
/// empty for a recipe nobody has generated against or whose lines resolve to nothing, and empty means "nothing
/// outstanding" rather than "not checked" — which is true of both, because both reads answer for the whole set
/// they were given.
/// </para>
/// </remarks>
/// <param name="Ai">
/// What AI work about this recipe is still undecided. Supplied by the Ai module's facade, which owns the meaning
/// of "outstanding".
/// </param>
/// <param name="AllergenReviewGaps">
/// The recognised ingredients whose allergen records are incomplete. Supplied by the Ingredients module's facade,
/// which owns the meaning of "reviewed" — and which names no allergen, so nothing here can become a claim about
/// the dish.
/// </param>
public sealed record RecipeReadinessExternalFacts(
    AiOutstandingSummaryServiceModel Ai,
    IReadOnlyList<IngredientAllergenReviewServiceModel> AllergenReviewGaps)
{
    /// <summary>
    /// What to evaluate with when neither module was asked — a recipe with no matched lines needs no allergen
    /// read, and a test of one rule needs no AI read.
    /// </summary>
    /// <remarks>
    /// Named rather than written out at each call site so that "nothing outstanding" has one spelling, and so the
    /// difference between it and a real empty answer is a decision somebody made rather than a default they got.
    /// </remarks>
    public static RecipeReadinessExternalFacts None { get; } =
        new(new AiOutstandingSummaryServiceModel(0, [], []), []);
}

namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// Whether a test run still has a problem nobody has decided anything about.
/// </summary>
/// <remarks>
/// <para>
/// <strong>"Unresolved" has exactly one meaning here</strong>, and it is the one
/// <see cref="Data.Entities.TestIssue"/> establishes: an issue is unresolved when no
/// <see cref="Data.Entities.TestIssueResolution"/> row exists for it. There is no flag to read and therefore no
/// second copy of the fact to drift from — this filter compiles into the same <c>NOT EXISTS</c> the entity's
/// remarks describe.
/// </para>
/// <para>
/// <strong>This is a statement about the tester's own record, never about the recipe.</strong> An unresolved
/// issue means somebody wrote down a problem and nobody has said what was done about it. It is not a readiness
/// verdict — that is 10.5's deterministic evaluator, which reads far more than this — and it is emphatically
/// not a food-safety, allergen or dietary finding (recipes.md).
/// </para>
/// <para>
/// An enum rather than a <c>bool</c>, for the reason <see cref="RecipeIngredientReviewFilter"/> gives: a
/// narrower question — runs with a <em>blocking</em> issue outstanding, say — can be added without turning a
/// shipped <c>bool</c> field into an enum, which api-contract.md counts as breaking.
/// </para>
/// <para>
/// Not persisted; this is a filter, never a column. Numbering starts at one so that a value nobody assigned is
/// out of range and fails loudly rather than defaulting to a claim about a test.
/// </para>
/// </remarks>
public enum TestRunIssueFilter
{
    /// <summary>At least one of the run's issues has no resolution recorded against it.</summary>
    HasUnresolved = 1,

    /// <summary>
    /// Every issue the run raised has been resolved. Also true of a run that raised none at all, which is
    /// vacuous but correct: there is nothing on it left to decide. A creator looking for "tests that found
    /// nothing wrong" wants <see cref="TestRunOutcome.Succeeded"/>, which is the tester's verdict rather than
    /// an absence of rows.
    /// </summary>
    AllResolved = 2,
}

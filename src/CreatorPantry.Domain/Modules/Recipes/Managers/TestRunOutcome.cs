namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// The tester's own verdict on a test run: how the cook went, in one word.
/// </summary>
/// <remarks>
/// <para>
/// Declared, never derived. A run with no issues recorded is not thereby a success — the tester may simply
/// not have written the issues down yet — and computing this from the rating or the issue list would put a
/// conclusion in the database that nobody actually reached.
/// </para>
/// <para>
/// Zero is <see cref="NotStated"/> deliberately, following <c>RecipeVersionReadiness</c>: an unset outcome
/// meaning "the tester has not said" under-claims, which is the safe direction. Contrast
/// <see cref="TestIssueSeverity"/>, where zero is refused outright because the same default would
/// under-report a blocking problem rather than merely decline to report one.
/// </para>
/// <para>
/// The numeric values are what existing rows store. Append new outcomes at the end rather than renumbering.
/// </para>
/// </remarks>
public enum TestRunOutcome
{
    /// <summary>The tester has not given a verdict. Never read as a pass.</summary>
    NotStated = 0,

    /// <summary>The recipe worked as written.</summary>
    Succeeded = 1,

    /// <summary>It worked, but something wants changing before the recipe is used as a source.</summary>
    SucceededWithIssues = 2,

    /// <summary>It did not work.</summary>
    Failed = 3,
}

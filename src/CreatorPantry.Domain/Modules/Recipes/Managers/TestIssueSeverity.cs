namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// How badly a problem found during a test affects the recipe.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Zero is not a severity,</strong> and <c>CK_TestIssues_Severity_Specified</c> refuses it. This is
/// the opposite call from <see cref="TestRunOutcome"/>, where zero means "not stated", and the difference is
/// the direction the default errs in: an unset severity landing on <see cref="Minor"/> would quietly
/// downgrade a blocking problem to a cosmetic one, and every reader after that point would believe it. The
/// same argument <c>RecipeAssetRole</c> makes about a link that would default to being the hero image.
/// </para>
/// <para>
/// Descriptive of the recipe's fitness as a draft, never of food safety. A <see cref="Blocking"/> issue means
/// the creator should not treat this version as a finished source; it is not a safety finding, and the absence
/// of one is not a safety clearance (recipes.md).
/// </para>
/// </remarks>
public enum TestIssueSeverity
{
    /// <summary>Worth noting and worth fixing, but the recipe works as written.</summary>
    Minor = 1,

    /// <summary>The recipe works, but somebody following it would get a noticeably worse result.</summary>
    Major = 2,

    /// <summary>The recipe should not be used as a source until this is dealt with.</summary>
    Blocking = 3,
}

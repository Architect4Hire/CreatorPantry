namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// How an issue raised by a test run was disposed of.
/// </summary>
/// <remarks>
/// <para>
/// Not every issue is closed by changing the recipe, and a model that could only record "fixed" would push
/// the other two outcomes into free text where nothing can count them. An issue nobody has dealt with yet has
/// no resolution row at all — that absence is what "unresolved" means, which is why there is no
/// <c>Deferred</c> member here.
/// </para>
/// <para>
/// Zero is refused by <c>CK_TestIssueResolutions_Kind_Specified</c>, for the reason
/// <see cref="TestIssueSeverity"/> gives: a resolution that defaulted to <see cref="Fixed"/> would claim a
/// correction that was never made.
/// </para>
/// </remarks>
public enum TestIssueResolutionKind
{
    /// <summary>The recipe was changed. Usually names the version carrying the correction.</summary>
    Fixed = 1,

    /// <summary>Real, understood, and deliberately left as it is.</summary>
    WontFix = 2,

    /// <summary>A later test did not show it again. Not a finding that the first test was wrong.</summary>
    NotReproduced = 3,
}

using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Recipes.Managers;

namespace CreatorPantry.Domain.Modules.Recipes.Data.Entities;

/// <summary>
/// Something a test run found wrong with the recipe. Interior to the <see cref="RecipeTestRun"/> aggregate for
/// its lifetime, addressable in its own right for resolution.
/// </summary>
/// <remarks>
/// <para>
/// <strong>An issue carries no resolved flag.</strong> Whether it has been dealt with is answered by whether a
/// <see cref="TestIssueResolution"/> row exists for it — one at most, enforced by a unique index. A boolean
/// here would be a second copy of that fact, and the two would eventually disagree; this way "unresolved" has
/// exactly one meaning and no write can drift from it.
/// </para>
/// <para>
/// Mutable, on the same terms as <see cref="TestObservation"/>: the tester may correct what they wrote.
/// Resolving the issue does not write here — it appends a resolution.
/// </para>
/// <para>
/// <see cref="RecipeId"/> is not a convenience column. It carries the run's recipe down one level so that the
/// resolution below can be constrained, by the database, to a correction version of this same recipe. See
/// <see cref="RecipeTestRun.RecipeId"/>.
/// </para>
/// </remarks>
public class TestIssue : IWorkspaceOwned
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    /// <inheritdoc cref="RecipeTestRun.RecipeId"/>
    public Guid RecipeId { get; set; }

    public Guid RecipeTestRunId { get; set; }

    /// <summary>
    /// How badly it affects the recipe. Never zero — see <see cref="TestIssueSeverity"/> for why this one
    /// default is refused where <see cref="TestRunOutcome"/>'s is not.
    /// </summary>
    public TestIssueSeverity Severity { get; set; }

    /// <summary>The problem in one line: "crumb too dense". Required.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>The problem at length, when one line is not enough.</summary>
    public string? Description { get; set; }

    /// <summary>
    /// The observation this issue was raised from, when it came from one. Null for an issue the tester filed
    /// directly.
    /// </summary>
    /// <remarks>
    /// The edge that makes "resolving an issue does not rewrite the observation" a statement with content:
    /// the two are connected, and the connection is a reference rather than a copy of the observation's text.
    /// Constrained within the workspace, so an issue cannot cite another workspace's note.
    /// </remarks>
    public Guid? TestObservationId { get; set; }

    /// <summary>Position within the run's issues, unique there.</summary>
    public int SortOrder { get; set; }

    /// <summary>
    /// The resolution, when one has been recorded. At most one; its absence is what "unresolved" means.
    /// </summary>
    public TestIssueResolution? Resolution { get; set; }
}

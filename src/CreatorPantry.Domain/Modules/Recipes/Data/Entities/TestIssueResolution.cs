using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Recipes.Managers;

namespace CreatorPantry.Domain.Modules.Recipes.Data.Entities;

/// <summary>
/// What was done about a <see cref="TestIssue"/>, and by whom. At most one per issue.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Immutable</strong> by way of <see cref="IImmutableRecord"/>: every update and delete is refused at
/// <c>SaveChanges</c>, whatever code path arrives there. This is the one frozen row in an otherwise editable
/// aggregate, and the asymmetry is the point — the observations and the issue around it are a tester still
/// describing what they saw, while this is somebody's decision about what to do, taken at a moment, naming a
/// version. Reopening an issue by quietly rewriting its resolution would make the record of that decision
/// untrue.
/// </para>
/// <para>
/// One per issue, enforced by <c>UX_TestIssueResolutions_Workspace_Issue</c> rather than by a check the
/// resolution seam performs. Resolving an already-resolved issue is therefore refused by the database, which
/// is what makes a replayed or duplicated command safe.
/// </para>
/// <para>
/// <strong>Writing one does not touch the issue or its observation.</strong> Nothing here copies their text,
/// and nothing about this row's creation modifies them — the guarantee 10.3 asks for, kept structurally by
/// the resolution being a separate row rather than a field on the issue.
/// </para>
/// <para>
/// <strong>Immutability and the delete seam.</strong> Deleting a <see cref="RecipeTestRun"/> is meant to take
/// these with it, and in the database it does — the cascade runs two levels down and
/// <see cref="IImmutableRecord"/> never sees it. But EF cascades to <em>tracked</em> children on the client
/// first, and a resolution that happens to be loaded is marked <c>Deleted</c> and refused. So a seam that
/// removes a test run must not have its resolutions loaded when it does.
/// </para>
/// <para>
/// That is a real constraint rather than a quirk to work around: the interceptor cannot distinguish a
/// cascade from a deliberate delete, and teaching it to would weaken the same guarantee for
/// <see cref="RecipeVersion"/> and the AI module's proposals.
/// <c>RecipeTestRunConstraintTests.A_run_delete_is_refused_while_its_resolutions_are_tracked</c> pins the
/// behaviour so the seam inherits the knowledge rather than rediscovering it.
/// </para>
/// </remarks>
public class TestIssueResolution : IWorkspaceOwned, IImmutableRecord
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    /// <inheritdoc cref="RecipeTestRun.RecipeId"/>
    public Guid RecipeId { get; set; }

    public Guid TestIssueId { get; set; }

    /// <summary>How the issue was disposed of.</summary>
    public TestIssueResolutionKind Kind { get; set; }

    /// <summary>What was done, in the words of whoever did it.</summary>
    public string? Notes { get; set; }

    /// <inheritdoc cref="Recipe.CreatedByMembershipId"/>
    public Guid ResolvedByMembershipId { get; set; }

    public DateTimeOffset ResolvedAt { get; set; }

    /// <summary>
    /// The recipe version carrying the correction, when there is one to point at.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Constrained to a version <em>of the same recipe, in the same workspace</em> by the composite foreign
    /// key <c>(WorkspaceId, RecipeId, ResolutionRecipeVersionId) -> RecipeVersions (WorkspaceId, RecipeId,
    /// Id)</c>. The requirement could have been met by a validator; making it a key means a resolution citing
    /// another recipe's version — or another workspace's — is unrepresentable rather than merely rejected by
    /// whichever code path happens to run.
    /// </para>
    /// <para>
    /// Null for <see cref="TestIssueResolutionKind.WontFix"/> and
    /// <see cref="TestIssueResolutionKind.NotReproduced"/>, which change no recipe, and legitimately null for
    /// a fix whose version has not been captured yet.
    /// </para>
    /// </remarks>
    public Guid? ResolutionRecipeVersionId { get; set; }

    /// <summary>
    /// Why a correction version older than the issue was accepted anyway; null when the question did not
    /// arise.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A version that predates the test that found the problem cannot contain its fix, so the ordinary answer
    /// is that this is a mistake. It is not always: a creator may be recording that an older version had it
    /// right all along and the regression came later. The override exists so that case can be stated rather
    /// than forced through, and it stores the reason rather than a flag — see
    /// <see cref="TestRunPolicy.PredatingVersionOverrideReasonMaxLength"/>.
    /// </para>
    /// <para>
    /// The comparison itself is between two rows' timestamps and so cannot be a check constraint. Business
    /// owns it; this column is only the record of the decision.
    /// </para>
    /// </remarks>
    public string? PredatingVersionOverrideReason { get; set; }
}

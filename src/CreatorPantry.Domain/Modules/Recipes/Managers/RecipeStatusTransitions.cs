using CreatorPantry.Domain.Managers.Persistence;

namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// One move a recipe may make between editorial states, and what it takes to make it.
/// </summary>
/// <remarks>
/// <para>
/// A rule rather than a pair of states, because every condition attached to a move belongs beside the move:
/// the role it needs, whether it must be explained, whether readiness has to be clear, and whether it writes
/// a version. A seam that read the pair and then looked the conditions up elsewhere would be a second place
/// for them to disagree.
/// </para>
/// </remarks>
/// <param name="MinimumRole">
/// The least workspace role that may make this move. Compared with <c>&gt;=</c>, which is what
/// <see cref="WorkspaceRole"/>'s ordering is for.
/// </param>
/// <param name="RequiresReason">
/// Whether the caller must say why. True only for a reopen: every other move is explained by the states it
/// runs between, and a reopen is the one a later reader cannot reconstruct — "this was approved and then it
/// was not" needs a sentence.
/// </param>
/// <param name="RequiresReadinessClear">
/// Whether the move needs a readiness evaluation with no blockers (TESTRUN-004). True only for the approval.
/// </param>
/// <param name="WritesVersion">
/// Whether the move captures a <c>RecipeVersion</c>. True only for the approval, so that an approval names
/// immutable content rather than whatever the recipe said at the time.
/// </param>
public sealed record RecipeStatusTransitionRule(
    RecipeStatus From,
    RecipeStatus To,
    WorkspaceRole MinimumRole,
    bool RequiresReason = false,
    bool RequiresReadinessClear = false,
    bool WritesVersion = false);

/// <summary>
/// Every move a recipe may make between editorial states (TESTRUN-005), and who may make it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The whole machine, in one place a reviewer can read.</strong> A move that is not listed here does
/// not exist: <see cref="Find"/> answers null and the seam refuses. That is what "invalid jumps fail" means
/// in practice, and stating the legal moves is the only way to get it — a list of illegal ones is a list
/// somebody eventually forgets to extend.
/// </para>
/// <para>
/// <strong>Forward one step at a time.</strong> <c>Draft → Testing</c> and <c>Testing → Approved</c> are
/// absent deliberately: each forward move is a claim about work that happened, and skipping one is a claim
/// nobody made. Backwards is different — a reopen goes straight to <see cref="RecipeStatus.InDevelopment"/>
/// from wherever the recipe had got to, because walking a rejected recipe back through
/// <see cref="RecipeStatus.Testing"/> would mean two commands to say one thing, and the intermediate state
/// would be a moment the recipe was never really in.
/// </para>
/// <para>
/// <strong>Archiving is part of this machine, not beside it.</strong> REC-006's archive and restore predate
/// TESTRUN-005 and moved the status directly; they now run through the same rules and write the same history,
/// so a recipe's editorial life reads as one sequence rather than two that have to be interleaved by
/// timestamp.
/// </para>
/// <para>
/// <strong>Versioned as a whole.</strong> <see cref="Version"/> travels on every transition record, so a
/// stored history stays interpretable after the machine changes shape — the same reason
/// <see cref="RecipeReadinessCatalogue.Version"/> travels on an evaluation. Adding or removing a move, or
/// changing a role or a gate, raises it.
/// </para>
/// </remarks>
public static class RecipeStatusTransitions
{
    /// <summary>
    /// The machine's version, recorded on every transition. Raised when a move is added or removed, or when
    /// one's role or gate changes.
    /// </summary>
    public const string Version = "1.0.0";

    /// <summary>
    /// Where a reopened recipe goes, and where an edit to an approved one sends it.
    /// </summary>
    /// <remarks>
    /// <see cref="RecipeStatus.InDevelopment"/> rather than the state it held before, for the reason
    /// <see cref="RecipePolicy.UnarchivedStatus"/> gives about the archive: nothing records what the previous
    /// state was, and a column whose only reader is this one line is not worth the drift. It is also the
    /// honest answer — a recipe that has come back off review is being worked on.
    /// </remarks>
    public const RecipeStatus ReopenTarget = RecipeStatus.InDevelopment;

    /// <summary>
    /// The states a recipe is moved out of when its content is edited, and <see cref="ReopenTarget"/> is
    /// where it goes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong><see cref="RecipeStatus.Approved"/> only.</strong> An approval is a claim that somebody with
    /// standing cleared <em>these words</em>, so an edit afterwards leaves the claim describing content nobody
    /// approved. Moving the recipe is the honest resolution, and it is recorded as a transition with a stated
    /// reason rather than applied silently.
    /// </para>
    /// <para>
    /// The earlier states are deliberately not here. <see cref="RecipeStatus.Testing"/> and
    /// <see cref="RecipeStatus.ReadyForReview"/> assert that work is underway, not that content was cleared,
    /// and editing a recipe while testing it is the normal way a test kitchen runs. Approval re-evaluates
    /// readiness from scratch anyway, so nothing an edit does can slip past the gate by way of those states.
    /// </para>
    /// </remarks>
    public static bool EditReopens(RecipeStatus status) => status is RecipeStatus.Approved;

    /// <summary>What an edit-driven reopen records as its reason.</summary>
    /// <remarks>
    /// A fixed sentence rather than the creator's words, because nobody typed anything: the edit seam moved
    /// the recipe as a consequence of a content change. It is in the domain rather than left to a client
    /// precisely because the reason column is required on a reopen and this reopen has no author to ask.
    /// </remarks>
    public const string EditReopenReason = "The recipe's content was edited after it was approved.";

    /// <summary>Every legal move, grouped by where it starts.</summary>
    public static readonly IReadOnlyList<RecipeStatusTransitionRule> All =
    [
        // ---- Forward, one step at a time. Contributor, because advancing your own work is the work. ----
        new(RecipeStatus.Draft, RecipeStatus.InDevelopment, WorkspaceRole.Contributor),
        new(RecipeStatus.InDevelopment, RecipeStatus.Testing, WorkspaceRole.Contributor),
        new(RecipeStatus.Testing, RecipeStatus.ReadyForReview, WorkspaceRole.Contributor),

        // ---- The approval. Editor, gated on readiness, and it writes the version it approves. ----
        new(
            RecipeStatus.ReadyForReview,
            RecipeStatus.Approved,
            WorkspaceRole.Editor,
            RequiresReadinessClear: true,
            WritesVersion: true),

        // ---- Reopen. Editor and explained, because it withdraws work somebody else advanced. ----
        new(RecipeStatus.Testing, ReopenTarget, WorkspaceRole.Editor, RequiresReason: true),
        new(RecipeStatus.ReadyForReview, ReopenTarget, WorkspaceRole.Editor, RequiresReason: true),
        new(RecipeStatus.Approved, ReopenTarget, WorkspaceRole.Editor, RequiresReason: true),

        // ---- Archive, from anywhere but the archive. Editor, as REC-006 already required. ----
        new(RecipeStatus.Draft, RecipeStatus.Archived, WorkspaceRole.Editor),
        new(RecipeStatus.InDevelopment, RecipeStatus.Archived, WorkspaceRole.Editor),
        new(RecipeStatus.Testing, RecipeStatus.Archived, WorkspaceRole.Editor),
        new(RecipeStatus.ReadyForReview, RecipeStatus.Archived, WorkspaceRole.Editor),
        new(RecipeStatus.Approved, RecipeStatus.Archived, WorkspaceRole.Editor),

        // ---- Restore. To Draft, which is RecipePolicy.UnarchivedStatus and the one target REC-006 has. ----
        new(RecipeStatus.Archived, RecipePolicy.UnarchivedStatus, WorkspaceRole.Editor),
    ];

    /// <summary>
    /// The rule for one move, or <c>null</c> when there is no such move.
    /// </summary>
    /// <remarks>
    /// Null rather than an exception: an invalid jump is something a caller asks for and is told about, not a
    /// defect. A move to the state the recipe is already in is also null — there is no rule for it — which is
    /// the right answer for a machine whose every move means something happened.
    /// </remarks>
    public static RecipeStatusTransitionRule? Find(RecipeStatus from, RecipeStatus to) =>
        Lookup.GetValueOrDefault((from, to));

    /// <summary>Every state a recipe in this one can move to, in the order <see cref="All"/> lists them.</summary>
    /// <remarks>
    /// Published so a surface can offer the moves that exist rather than offering all of them and letting the
    /// server refuse most — and so a test can assert the shape of the machine from one state at a time.
    /// </remarks>
    public static IReadOnlyList<RecipeStatus> From(RecipeStatus status) =>
        [.. All.Where(rule => rule.From == status).Select(rule => rule.To)];

    private static readonly IReadOnlyDictionary<(RecipeStatus From, RecipeStatus To), RecipeStatusTransitionRule>
        Lookup = All.ToDictionary(rule => (rule.From, rule.To));
}

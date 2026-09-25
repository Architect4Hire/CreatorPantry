using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Ai.Managers;

namespace CreatorPantry.Domain.Modules.Ai.Data.Entities;

/// <summary>
/// One request for AI assistance and everything that happened to it: what was asked, of what, by whom, and
/// how it ended. Workspace-owned creator intellectual property, and an aggregate root.
/// </summary>
/// <remarks>
/// <para>
/// <strong>There is nowhere here to put a prompt body, a model response, or a rendered template.</strong>
/// That is the point rather than an omission: ai.md forbids logging private prompt bodies and generated
/// creator content by default, and a table with no column for them cannot accumulate them by accident. What
/// an operation records is its shape — task, scope, source, actor, timing, outcome — which is everything
/// needed to explain it without quoting it. Execution detail and the proposal's own content belong to their
/// own entities.
/// </para>
/// <para>
/// <strong>The status is the operation's whole lifecycle,</strong> and
/// <see cref="AiOperationTransitionPolicy"/> is the only thing that decides how it moves. Five of the eight
/// states are terminal; a retry is a new row with a new <see cref="IdempotencyKey"/>, never a revived one,
/// because a row that changed its mind about what happened to it would make the provenance recorded against
/// an accepted proposal ambiguous.
/// </para>
/// <para>
/// <see cref="RecipeId"/> and <see cref="RecipeVersionId"/> are nullable because this aggregate is not the
/// recipe module's. Later task types target content projects, briefs and media, and an operation that names
/// no recipe is a normal one rather than an incomplete one. Both references are composite foreign keys
/// carrying <see cref="WorkspaceId"/>, so an operation pointing at another workspace's recipe is
/// unreferenceable at the database rather than only refused by the code above it.
/// </para>
/// </remarks>
public class AiOperation : IWorkspaceOwned
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    /// <summary>
    /// Which capability this runs. Chosen by the server from an allow-list — never taken from a request
    /// field, and never from the model.
    /// </summary>
    public AiTaskType TaskType { get; set; }

    /// <summary>
    /// What the resulting proposal is allowed to touch. Fixed before the provider is called, so the bound
    /// cannot be argued backwards out of the output.
    /// </summary>
    public AiOperationScope Scope { get; set; }

    public AiOperationStatus Status { get; set; }

    /// <summary>The recipe this is about, when it is about one.</summary>
    public Guid? RecipeId { get; set; }

    /// <summary>
    /// The exact version the request was made against; null when the task is not version-specific.
    /// </summary>
    /// <remarks>
    /// An exact version rather than "latest", for the reason recipes.md gives every other version reference:
    /// a proposal computed against a recipe that has since changed is stale, and only a fixed source version
    /// lets the server find that out instead of applying a diff to something it was never computed from.
    /// </remarks>
    public Guid? RecipeVersionId { get; set; }

    /// <summary>
    /// The caller's key for this request, unique within the workspace. One key means one operation.
    /// </summary>
    /// <remarks>
    /// Required, and permanent. The generic idempotency record already makes the HTTP command replay-safe,
    /// but it expires; this column does not, so a replay arriving after that record has aged out still finds
    /// the operation it already created rather than starting a second one. A caller with no natural key is
    /// given a generated one at the seam rather than being allowed to omit it, because a nullable key would
    /// need a filtered unique index and would make "no key" mean "never replay-safe".
    /// </remarks>
    public string IdempotencyKey { get; set; } = string.Empty;

    /// <summary>
    /// The <c>WorkspaceMembership</c> of the creator who asked, not their Identity user id. Membership is the
    /// workspace-scoped identity (auth.md).
    /// </summary>
    /// <remarks>
    /// Not a foreign key, so authorship survives a member leaving — the same trade the recipe aggregate
    /// makes. It must come from <see cref="IWorkspaceContext.MembershipId"/> and never from a request field.
    /// </remarks>
    public Guid RequestedByMembershipId { get; set; }

    public DateTimeOffset RequestedAt { get; set; }

    /// <summary>
    /// When <see cref="Status"/> last changed. What an expiry sweep reads, so it does not have to decide
    /// which of the other timestamps is the relevant one for the state a row happens to be in.
    /// </summary>
    public DateTimeOffset StatusChangedAt { get; set; }

    /// <summary>
    /// When a worker first began running this. Stays set if the operation is later returned to the queue by
    /// lease recovery — it records that work started, not that it is still running.
    /// </summary>
    public DateTimeOffset? StartedAt { get; set; }

    /// <summary>When the operation reached a terminal state. Set in exactly those states, and never unset.</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>
    /// Why it failed, in terms stable enough to route on. Required when <see cref="Status"/> is
    /// <see cref="AiOperationStatus.Failed"/> and forbidden otherwise, so the two columns cannot disagree.
    /// </summary>
    public AiFailureCategory? FailureCategory { get; set; }

    /// <summary>
    /// How many times this has been claimed by a worker.
    /// </summary>
    /// <remarks>
    /// The bound on the lease-recovery cycle. <see cref="AiOperationStatus.Running"/> may return to
    /// <see cref="AiOperationStatus.Requested"/> when a worker dies mid-flight, and without this a task that
    /// kills its worker every time would cycle between those states indefinitely, spending a provider budget
    /// on each pass. At <c>AiPolicy.MaxAttempts</c> the operation fails with
    /// <see cref="AiFailureCategory.LeaseAbandoned"/> instead of being requeued again.
    /// </remarks>
    public int Attempts { get; set; }

    /// <summary>
    /// Not claimable before this instant: the request time initially, then a backoff instant after a requeue.
    /// </summary>
    public DateTimeOffset AvailableAt { get; set; }

    /// <summary>The claim token of the worker currently holding this, while <see cref="Status"/> is Running.</summary>
    /// <remarks>
    /// Checked on every write that completes an operation. A worker whose lease has since been taken by
    /// another must not be able to store a proposal over the top of it — the token is what makes that
    /// detectable rather than a last-writer-wins race.
    /// </remarks>
    public Guid? LeasedBy { get; set; }

    /// <summary>When the current claim lapses and another worker may take the operation.</summary>
    public DateTimeOffset? LeaseExpiresAt { get; set; }

    /// <summary>
    /// Optimistic concurrency token. Not for collaborative editing — for competing workers: two of them
    /// claiming one queued operation must not both win, and the loser needs a conflict rather than a silent
    /// overwrite of the other's claim.
    /// </summary>
    public byte[] RowVersion { get; set; } = [];
}

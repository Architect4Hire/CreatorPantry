using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Media.Managers;

namespace CreatorPantry.Domain.Modules.Media.Data.Entities;

/// <summary>
/// One request to generate images from one prompt (IMG-003), and the lineage of what was asked.
/// </summary>
/// <remarks>
/// <para>
/// <strong>It carries the prompt as text and names no <c>PromptRecord</c>, which is a decision rather than an
/// omission.</strong> A prompt record is <see cref="IImmutableRecord"/>, so its
/// <c>GeneratedImageId</c> can only ever be set at insert — which means the prompt row is written
/// <em>after</em> an image is committed, not before. An operation holding a foreign key to it would close a
/// cycle (prompt → image → operation → prompt) that could never be fully populated in either direction. The
/// text here is what was actually sent; <see cref="AiProposalId"/> is where it came from.
/// </para>
/// <para>
/// <strong>No bytes and no URL.</strong> This table and its images hold metadata; the files live in private
/// staging storage under the opaque key on <see cref="GeneratedImage.ObjectKey"/>, and nothing here is or
/// becomes a public address (media.md).
/// </para>
/// <para>
/// <strong>Mutable, unlike a prompt record.</strong> A status moves, a lease is taken and renewed, and a
/// completion time is stamped — so this carries no <see cref="IImmutableRecord"/> marker and
/// <c>ImmutableRecordInterceptor</c> leaves it alone.
/// </para>
/// </remarks>
public sealed class GeneratedImageOperation : IWorkspaceOwned
{
    public Guid Id { get; set; }

    /// <summary>Stamped by <c>WorkspaceOwnershipInterceptor</c> from the resolved context, never by hand.</summary>
    public Guid WorkspaceId { get; set; }

    public GeneratedImageOperationStatus Status { get; set; }

    /// <summary>
    /// The prompt as it was sent to the provider, after any creator edit.
    /// </summary>
    /// <remarks>
    /// Authoritative, the same way <c>PromptRecord.Text</c> is: a creator's edit of a composed prompt is what
    /// gets generated from, and a row recording the model's draft instead would misreport what produced the
    /// image.
    /// </remarks>
    public string PromptText { get; set; } = string.Empty;

    /// <summary>What the provider should avoid, when the request carried any. Null when it did not.</summary>
    public string? AvoidText { get; set; }

    /// <summary>
    /// The IMG-002 proposal the prompt was composed from, when it was composed rather than written.
    /// </summary>
    /// <remarks>
    /// Workspace-paired to <c>AiProposal</c>, so another workspace's proposal is unrepresentable rather than
    /// merely refused. Null for a prompt the creator wrote themselves.
    /// </remarks>
    public Guid? AiProposalId { get; set; }

    /// <summary>
    /// The recipe these pictures were asked for, when they were asked for one.
    /// </summary>
    /// <remarks>
    /// What the run was <em>for</em>, recorded when it was requested and never changed. It grounds nothing:
    /// the worker sends <see cref="PromptText"/> and does not read the recipe. It is here so a run can be
    /// traced to the work that asked for it without going through a proposal that a hand-written prompt
    /// does not have.
    /// </remarks>
    public Guid? RecipeId { get; set; }

    /// <summary>The exact version of <see cref="RecipeId"/> the request named. Never set without it.</summary>
    public Guid? RecipeVersionId { get; set; }

    /// <summary>The piece of work these pictures were asked for, when they were asked for one.</summary>
    public Guid? CreativeContextId { get; set; }

    /// <summary>How many images were asked for. Between one and four (<see cref="MediaPolicy"/>).</summary>
    public int VariantCount { get; set; }

    /// <summary>
    /// The quality class the pictures are made at, resolved at the request — the one asked for, or the default
    /// (AP.3.1). Null for a request made on a host that offers no classes, or before this column existed.
    /// </summary>
    /// <remarks>
    /// The class, not what it sends: the worker resolves the model and settings from configuration when it
    /// runs, and the hold already fixed the price per picture, so a remapped class cannot re-price a request
    /// already admitted. Part of what a key names (12.10l): the same key with a different class is refused.
    /// </remarks>
    public GeneratedImageQualityClass? QualityClass { get; set; }

    /// <summary>The provider the operation was run against, once a worker has chosen one.</summary>
    public string? ProviderName { get; set; }

    /// <summary>The model it asked for, once known.</summary>
    public string? ModelName { get; set; }

    /// <summary>The deployment that served it, when the provider distinguishes one.</summary>
    public string? ModelDeployment { get; set; }

    /// <summary>
    /// What went wrong, as a stable category. Null while nothing has.
    /// </summary>
    public string? FailureCategory { get; set; }

    /// <summary>A sanitized summary of the failure. Never the provider's raw body, which can quote the prompt.</summary>
    public string? FailureSummary { get; set; }

    /// <summary>
    /// The key that makes a retry return the first answer rather than buy a second generation.
    /// </summary>
    /// <remarks>
    /// Required and unique within the workspace. Image generation is the most expensive thing this product
    /// does, so a lost response must not become a second charge.
    /// </remarks>
    public string IdempotencyKey { get; set; } = string.Empty;

    /// <summary>
    /// The hold this generation's pictures are paid for from (AP.2.5). Null only for a generation admitted
    /// before this column existed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Set before the row is saved, so an operation that exists always names the hold that paid for it: a
    /// queued generation with no hold would make pictures nobody was charged for. The other direction cannot
    /// be made atomic — the hold commits first, because the period's row version needs its own transaction —
    /// so a hold whose operation never got written is released on the way out of
    /// <c>GeneratedImageGenerationDataLayer.RequestAsync</c>, with the expiry sweep behind that.
    /// </para>
    /// <para>
    /// An id, not a foreign key, and deliberately: the reservation belongs to the account rather than to this
    /// workspace, and a workspace deletion cascading into an account's allowance is the thing
    /// <c>AccountAiUsageEntry</c> declares no relationships to avoid.
    /// </para>
    /// </remarks>
    public Guid? AiQuotaReservationId { get; set; }

    /// <summary>
    /// How many variants the provider has generated and billed for, over every pass this operation has taken.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Cumulative, and that is what makes a resumed generation charge each picture once.</strong> A
    /// variant whose bytes could not be staged leaves no <c>GeneratedImage</c> row, so the rows are not a
    /// record of what was billed; a later pass re-runs that variant and the provider bills again, which this
    /// counts again because it genuinely happened twice.
    /// </para>
    /// <para>
    /// It counts what the provider <em>generated</em>. A call that failed before generation —
    /// rate-limited, unreachable, refused outright — is not in it, and its share of the hold is released.
    /// </para>
    /// </remarks>
    public int BilledVariantCount { get; set; }

    /// <summary>
    /// How many of <see cref="BilledVariantCount"/> nothing could be staged from: bytes that did not hold
    /// together, a picture too large, or one the provider flagged.
    /// </summary>
    /// <remarks>
    /// Charged all the same — the provider made the picture and bills for it — and recorded apart so the
    /// ledger can say so rather than reporting every charge as a success.
    /// </remarks>
    public int UnusableVariantCount { get; set; }

    /// <summary>
    /// How many of <see cref="UnusableVariantCount"/> the provider withheld on its own safety grounds.
    /// </summary>
    public int FlaggedVariantCount { get; set; }

    public Guid RequestedByMembershipId { get; set; }

    /// <summary>
    /// The Identity account that asked, recorded with <see cref="RunningImagesCap"/> so the queue can hold that
    /// account to its plan's running pictures across every workspace it works in (AP.4.2). Null with it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// From <c>IWorkspaceContext.AccountId</c> in the request scope — never a request field — and the initiating
    /// account auth.md asks a background job to record. The worker never charges or authorizes by it: it runs as a
    /// service identity, and the allowance was held at the request.
    /// </para>
    /// <para>
    /// Read only by the claim, as an identifier compared with other rows' and never returned. Account erasure
    /// (9A.3) clears it with the account's quota and plan rows.
    /// </para>
    /// </remarks>
    public string? RequestedByAccountId { get; set; }

    /// <summary>
    /// How many pictures <see cref="RequestedByAccountId"/>'s plan let it have generating at once when this was
    /// asked for (AP.4.2); null when no plan applied, and the workspace's flat cap alone holds it.
    /// </summary>
    /// <remarks>
    /// Taken at the request rather than read at the claim, because the claim cannot ask a plan anything — it is a
    /// query, and it runs before the worker knows whose work it is. So a plan changed while this waits does not
    /// change it: the next request carries the new number.
    /// </remarks>
    public int? RunningImagesCap { get; set; }

    public DateTimeOffset RequestedAt { get; set; }

    public DateTimeOffset StatusChangedAt { get; set; }

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>How many times a worker has claimed this. Drives the requeue backoff.</summary>
    public int Attempts { get; set; }

    /// <summary>When a worker may claim it. Moved forward by the backoff after a failed attempt.</summary>
    public DateTimeOffset AvailableAt { get; set; }

    /// <summary>The worker holding it, when one does.</summary>
    public Guid? LeasedBy { get; set; }

    /// <summary>When that lease lapses, so an abandoned claim can be recovered.</summary>
    public DateTimeOffset? LeaseExpiresAt { get; set; }
}

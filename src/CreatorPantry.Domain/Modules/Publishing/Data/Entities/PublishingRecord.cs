using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Publishing.Managers;

namespace CreatorPantry.Domain.Modules.Publishing.Data.Entities;

/// <summary>
/// One confirmed intent to publish one piece of content to one profile at one time, and everything that has
/// happened to it since (PUB-002/003, 13.10). Workspace-owned, and the root of the ledger aggregate.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Provider-neutral, and it holds no provider id.</strong> What the provider calls the post it made
/// from this row is in <see cref="ExternalPublicationReference"/> and nowhere else (publishing.md,
/// external.md). Everything above the data layer names a record by <see cref="Id"/>, which CreatorPantry made.
/// </para>
/// <para>
/// <strong>The payload is a snapshot, written once.</strong> <see cref="ContentText"/>, the picture, the
/// profile, the platform and the schedule are what the creator confirmed, and they are what was sent to a
/// third party. An edit to the card afterwards is a new confirmation and a new row here — it never rewrites
/// a queued payload, which is the one thing publishing.md asks a confirmation to guarantee. Only the
/// lifecycle columns move after the insert.
/// </para>
/// <para>
/// <strong><see cref="Platform"/> is copied rather than read through the profile.</strong>
/// <see cref="PublishingProfile"/> is a cache of what the provider last said and every successful read
/// rewrites it, so a record that asked the profile where it went could answer differently next week.
/// </para>
/// <para>
/// <strong>Never deleted to tidy up.</strong> A published row is the only record CreatorPantry holds that it
/// sent something to a third party. Rows leave by the workspace's own cascade or a documented erasure path,
/// and by nothing else; see the retention note on <see cref="PublishingLedgerPolicy"/>.
/// </para>
/// </remarks>
public class PublishingRecord : IWorkspaceOwned
{
    public Guid Id { get; set; }

    /// <summary>Stamped by <c>WorkspaceOwnershipInterceptor</c> from the resolved context, never by hand.</summary>
    public Guid WorkspaceId { get; set; }

    // ---- what is being published, and where ----

    /// <summary>
    /// The board card this was confirmed from. Required on creation; nullable only so it can outlive the plan.
    /// </summary>
    /// <remarks>
    /// Business requires one when it writes the row — nothing creates a publishing record except a card's
    /// confirmation. The column is nullable because 13.6c decided that a delivered or failed publication does
    /// not block deleting its card, and a record that recorded a real delivery must not go with the plan: that
    /// delete detaches the terminal rows it finds and refuses while any row is still live. Restricted rather
    /// than set-null in the store, because SQL Server allows one action path from <c>Workspaces</c> into this
    /// table and the workspace's own cascade is it.
    /// </remarks>
    public Guid? ContentCardId { get; set; }

    /// <summary>The place it goes. Workspace-paired, so another workspace's profile is unrepresentable.</summary>
    public Guid PublishingProfileId { get; set; }

    /// <summary>The network it was confirmed for, as the profile read at that moment.</summary>
    public PublishingPlatform Platform { get; set; }

    /// <summary>
    /// The text as confirmed, which is the text that is sent. The creator's own words, copied deliberately.
    /// </summary>
    /// <remarks>
    /// The one place in this product where creator text is copied rather than pointed at, and for a reason
    /// content.md allows: this is not a derivative that could compete with its source but a record of what
    /// left the building. Re-deriving it from the card later would misreport what was published.
    /// </remarks>
    public string ContentText { get; set; } = string.Empty;

    /// <summary>The library asset that goes with it, when one does.</summary>
    public Guid? MediaAssetId { get; set; }

    /// <summary>The exact version of <see cref="MediaAssetId"/>. Never set without it.</summary>
    public int? MediaAssetVersionNumber { get; set; }

    // ---- when ----

    /// <summary>The instant it is to be published, in UTC.</summary>
    public DateTimeOffset ScheduledAt { get; set; }

    /// <summary>
    /// The IANA zone the creator scheduled in, read from <c>BrandProfile.TimeZoneId</c> (publishing.md).
    /// </summary>
    /// <remarks>
    /// Required, and deliberately not defaulted: a schedule whose zone was guessed cannot be shown back to the
    /// creator as what they asked for. 13.10a refuses a confirmation from a workspace that has not set one.
    /// </remarks>
    public string ScheduleTimeZoneId { get; set; } = string.Empty;

    /// <summary>
    /// The local wall-clock time the creator chose, with no offset of its own — their intent, kept beside the
    /// instant it resolved to so a zone's rules changing cannot make the stored schedule unexplainable.
    /// </summary>
    public DateTime ScheduledLocal { get; set; }

    // ---- where it stands ----

    public PublishingRecordStatus Status { get; set; }

    public DateTimeOffset StatusChangedAt { get; set; }

    /// <summary>What went wrong, as a stable category. Null while nothing has.</summary>
    public string? FailureCategory { get; set; }

    /// <summary>A sanitized summary. Never the provider's raw body, which can quote the content or a credential.</summary>
    public string? FailureSummary { get; set; }

    // ---- attempts, and not knowing ----

    /// <summary>How many times the provider has been asked to take this post. Bounds the retry.</summary>
    public int SubmitAttempts { get; set; }

    /// <inheritdoc cref="SubmitAttempts"/>
    public int CancelAttempts { get; set; }

    public DateTimeOffset? LastAttemptAt { get; set; }

    /// <summary>
    /// The operation whose outcome is not known, or null when the record's state is what it says.
    /// </summary>
    /// <remarks>
    /// A timeout is not a failure and not a success. Recording which call was in flight — and since when — is
    /// what lets reconciliation ask the provider what actually happened instead of guessing, and what stops
    /// an uncertain submit from being retried into a second post (publishing.md).
    /// </remarks>
    public PublishingOperation? UncertainOperation { get; set; }

    /// <inheritdoc cref="UncertainOperation"/>
    public DateTimeOffset? UncertainSince { get; set; }

    /// <summary>When a worker may claim it. Moved forward by the backoff after an attempt.</summary>
    public DateTimeOffset AvailableAt { get; set; }

    /// <summary>The worker holding it, when one does.</summary>
    public Guid? LeasedBy { get; set; }

    /// <summary>When that lease lapses, so an abandoned claim can be recovered.</summary>
    public DateTimeOffset? LeaseExpiresAt { get; set; }

    // ---- who, when, and the guards ----

    /// <summary>
    /// The member who confirmed it. Not a foreign key: authorship outlives membership, as elsewhere.
    /// </summary>
    /// <remarks>
    /// Every row has one, because publishing requires an explicit confirmation and no automation policy exists
    /// to approve one instead. A policy column would be a column that can only be null; it arrives with the
    /// first policy (publishing.md).
    /// </remarks>
    public Guid ConfirmedByMembershipId { get; set; }

    /// <summary>When it was confirmed.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the provider accepted it, or null while none has.</summary>
    public DateTimeOffset? SubmittedAt { get; set; }

    /// <summary>When it reached a state nothing further is expected from.</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>
    /// The key that makes a replayed confirmation return the first record rather than buy a second post.
    /// </summary>
    /// <remarks>
    /// Required and unique within the workspace. The shared <c>IdempotencyRecord</c> replays a result for a
    /// day and then expires; a confirmation replayed after that must still not publish twice, so the durable
    /// guard has to be this column's index.
    /// </remarks>
    public string IdempotencyKey { get; set; } = string.Empty;

    public byte[] RowVersion { get; set; } = [];
}

/// <summary>
/// The integration record mapping a provider's post to the <see cref="PublishingRecord"/> that asked for it
/// (external.md). Workspace-owned.
/// </summary>
/// <remarks>
/// The only table that holds a provider's identifier for a post. One row per record, and one row per provider
/// post: the unique indexes are what make a replayed or reconciled submit resolve to the post it already made
/// instead of a second one. Never returned by a facade and never logged.
/// </remarks>
public class ExternalPublicationReference : IWorkspaceOwned
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    public Guid PublishingRecordId { get; set; }

    public string ProviderKey { get; set; } = string.Empty;

    /// <summary>The provider's id for the post — Buffer calls it an update. Never leaves the server.</summary>
    public string ExternalUpdateId { get; set; } = string.Empty;

    /// <summary>When CreatorPantry first learned this id, whether from the submit or from a reconciliation.</summary>
    public DateTimeOffset FirstSeenAt { get; set; }

    /// <summary>When the provider last confirmed the post still exists, or null if it has not been asked since.</summary>
    public DateTimeOffset? LastConfirmedAt { get; set; }
}

/// <summary>
/// One call to the provider about one <see cref="PublishingRecord"/>, and how it ended. Workspace-owned and
/// write-once.
/// </summary>
/// <remarks>
/// <para>
/// The <c>DeliveryAttempt</c> publishing.md asks for: the durable, auditable trail behind a record's current
/// state, so "this was refused twice and then timed out" is answerable without a log search. Append-only —
/// <see cref="IImmutableRecord"/> means <c>ImmutableRecordInterceptor</c> refuses every update and delete.
/// </para>
/// <para>
/// <strong>Outcomes only.</strong> A category, a status code and a sanitized summary; never a request body, a
/// response body, a header or a credential. There is nothing here to redact later because nothing sensitive
/// is written in the first place.
/// </para>
/// <para>
/// Bounded by construction rather than pruned: the submit and cancel caps and the reconciliation window on
/// <see cref="PublishingLedgerPolicy"/> put a ceiling on how many rows one record can accumulate.
/// </para>
/// </remarks>
public class PublishingDeliveryAttempt : IWorkspaceOwned, IImmutableRecord
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    public Guid PublishingRecordId { get; set; }

    /// <summary>Its place in this record's trail, from one. Unique per record by index rather than by agreement.</summary>
    public int AttemptNumber { get; set; }

    public PublishingOperation Operation { get; set; }

    public PublishingAttemptOutcome Outcome { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    /// <summary>When the call returned, or null when nothing ever came back.</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>The provider's HTTP status, when there was one. Null for a call that did not return.</summary>
    public int? ProviderStatusCode { get; set; }

    /// <summary>A stable category, matching the one the record carries when this attempt is the latest.</summary>
    public string? FailureCategory { get; set; }

    /// <inheritdoc cref="PublishingRecord.FailureSummary"/>
    public string? FailureSummary { get; set; }

    /// <summary>The request this attempt belonged to, so it joins up with the traces and the audit trail.</summary>
    public Guid CorrelationId { get; set; }
}

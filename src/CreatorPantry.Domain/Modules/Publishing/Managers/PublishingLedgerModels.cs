namespace CreatorPantry.Domain.Modules.Publishing.Managers;

/// <summary>
/// Where one publishing record stands. Stored as its integer, so members are appended and never renumbered;
/// zero is not a status.
/// </summary>
/// <remarks>
/// <para>
/// publishing.md's lifecycle, with the states this product can actually reach:
/// </para>
/// <code>
/// Pending ──► Submitting ──► Scheduled ──► Published        (terminal)
///    │             │             │  └────► Failed           (terminal)
///    │             ├──► Failed   └──► Cancelling ──► Cancelled
///    │             └──► NeedsAttention            └──► Scheduled   (the cancel was refused)
///    └──► Cancelled  (never sent)
/// NeedsAttention ──► Scheduled | Published | Cancelled | Failed   (reconciliation found out)
/// </code>
/// <para>
/// <strong><see cref="NeedsAttention"/> is the honest answer, not a tidy one.</strong> An outcome nobody
/// knows is never recorded as success or failure for convenience: a post may exist at the provider, so a
/// human — or a reconciliation that can ask — resolves it. This is the state that keeps that promise.
/// </para>
/// </remarks>
public enum PublishingRecordStatus
{
    /// <summary>Confirmed by a creator. Nothing has been sent to a provider.</summary>
    Pending = 1,

    /// <summary>A submit call is in flight, held by the worker named on the record's lease.</summary>
    Submitting = 2,

    /// <summary>The provider accepted it and will post it. Its external reference exists.</summary>
    Scheduled = 3,

    /// <summary>The provider reports it posted.</summary>
    Published = 4,

    /// <summary>A cancellation has been asked for and the provider has not answered yet.</summary>
    Cancelling = 5,

    /// <summary>It will not be posted: cancelled before submit, or the provider confirmed it is gone.</summary>
    Cancelled = 6,

    /// <summary>The provider refused it, or posted nothing and said why. Another go is a new confirmation.</summary>
    Failed = 7,

    /// <summary>
    /// The outcome is not known and cannot be settled automatically — a call that never returned, or a retry
    /// budget spent. Someone has to look at the provider.
    /// </summary>
    NeedsAttention = 8,
}

/// <summary>What a provider was asked to do about a publishing record.</summary>
public enum PublishingOperation
{
    /// <summary>Take this post and schedule it.</summary>
    Submit = 1,

    /// <summary>Remove a post that has not gone out yet.</summary>
    Cancel = 2,

    /// <summary>Tell us what became of a post we already handed over. Read-only, and always safe to repeat.</summary>
    Reconcile = 3,
}

/// <summary>How one call to a provider ended, in CreatorPantry's own vocabulary.</summary>
/// <remarks>
/// A provider's status code is recorded beside this, but the decision about what to do next is made from this
/// enum: mapping happens once, at the integration boundary (external.md).
/// </remarks>
public enum PublishingAttemptOutcome
{
    /// <summary>The provider did what was asked.</summary>
    Accepted = 1,

    /// <summary>The provider answered and said no. Repeating the same call will get the same answer.</summary>
    Refused = 2,

    /// <summary>The provider is throttling. Safe to repeat later, and only later.</summary>
    RateLimited = 3,

    /// <summary>The provider could not be reached, before it could have acted.</summary>
    Unavailable = 4,

    /// <summary>
    /// Nothing came back, or what came back says nothing about whether the provider acted. The outcome is
    /// unknown — this is what puts the record's uncertainty columns on.
    /// </summary>
    Unknown = 5,
}

/// <summary>Limits, caps and the retention rule for the publishing ledger (13.10).</summary>
/// <remarks>
/// <para>
/// <strong>Retention: a publishing record is never deleted to tidy up.</strong> It is the only record
/// CreatorPantry holds that it handed content to a third party, and publishing.md asks for auditable
/// outcomes. A row leaves exactly two ways: the workspace is erased, which cascades, or a documented erasure
/// path removes it. Deleting the board card it came from does neither — that detaches it (13.6c).
/// </para>
/// <para>
/// <strong>Its attempts are bounded rather than pruned.</strong> <see cref="MaxSubmitAttempts"/>,
/// <see cref="MaxCancelAttempts"/> and <see cref="ReconcileWindow"/> put a ceiling on how many attempt rows
/// one record can accumulate, so no sweeper job has to own them and no history is lost to one. The backoff
/// and lease timings that spend that budget belong to the reconciliation worker (13.10c), not here.
/// </para>
/// <para>
/// <strong>And there is nothing sensitive stored to expire.</strong> No request or response body, no header
/// and no credential is written anywhere in this aggregate, so retention is a question about creator history
/// alone.
/// </para>
/// </remarks>
public static class PublishingLedgerPolicy
{
    /// <summary>
    /// The longest content a publishing record can carry, in UTF-16 units.
    /// </summary>
    /// <remarks>
    /// Matched to <c>ContentPolicy.SocialBodyMaxLength</c>, the ceiling the social write seam takes, because
    /// what a creator can write is what they must be able to publish. Stated here rather than referenced
    /// because the Content module's policy is module-internal and may not cross a boundary; a test asserts
    /// the two numbers still agree.
    /// </remarks>
    public const int ContentTextMaxLength = 20000;

    /// <summary>
    /// How many of a card's publications one read returns, newest scheduled first.
    /// </summary>
    /// <remarks>
    /// A cap rather than a page, because a card is one piece of work: a creator with more confirmed
    /// publications than this for a single card is telling us something other than "I need page two".
    /// </remarks>
    public const int RecordsPerCard = 50;

    /// <summary>A stable failure category, such as <c>provider_refused</c>. Not a message.</summary>
    public const int FailureCategoryMaxLength = 64;

    /// <summary>A sanitized one-line summary. Never a provider body.</summary>
    public const int FailureSummaryMaxLength = 500;

    /// <summary>How many times the provider is asked to take one post before the record needs attention.</summary>
    public const int MaxSubmitAttempts = 5;

    /// <inheritdoc cref="MaxSubmitAttempts"/>
    public const int MaxCancelAttempts = 5;

    /// <summary>
    /// How far ahead of now a publication may be scheduled.
    /// </summary>
    /// <remarks>
    /// A floor rather than a courtesy: a post confirmed for a moment that passes while the submit is in
    /// flight is one the provider may refuse for being in the past, after CreatorPantry has already committed
    /// a row saying it was asked for.
    /// </remarks>
    public static readonly TimeSpan MinimumScheduleLead = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The furthest ahead a publication may be scheduled.
    /// </summary>
    /// <remarks>
    /// The provider documents no horizon, so this is CreatorPantry's own: a year is longer than any editorial
    /// calendar this product plans, and it keeps a typo in a year from parking a row in the ledger until
    /// nobody is left who remembers confirming it.
    /// </remarks>
    public static readonly TimeSpan MaximumScheduleHorizon = TimeSpan.FromDays(365);

    /// <summary>How often the reconciliation worker looks for records with something outstanding.</summary>
    /// <remarks>
    /// It costs the provider nothing when there is no due work: a pass that claims no record makes no call.
    /// </remarks>
    public static readonly TimeSpan ReconcilePollingInterval = TimeSpan.FromMinutes(1);

    /// <summary>How often lapsed claims are swept back into the queue.</summary>
    public static readonly TimeSpan ReconcileMaintenanceInterval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long a worker holds a claimed record before another worker may take it.
    /// </summary>
    /// <remarks>
    /// Comfortably longer than a provider call and its retries, so a lease lapses because a worker died rather
    /// than because the provider was slow.
    /// </remarks>
    public static readonly TimeSpan ReconcileLeaseDuration = TimeSpan.FromMinutes(5);

    /// <summary>How many records one pass claims before it stops.</summary>
    public const int ReconcileClaimBatchSize = 25;

    /// <summary>
    /// How long after a call a complete listing's silence is taken as proof that no post was made.
    /// </summary>
    /// <remarks>
    /// The grace exists because a provider's own listing may lag what it just created. Concluding "never
    /// made" is the one conclusion that can lose a creator's post, so it is drawn only from a listing read to
    /// its end, and only once the provider has had this long to start admitting the post exists.
    /// </remarks>
    public static readonly TimeSpan AbsenceGrace = TimeSpan.FromMinutes(5);

    /// <summary>How long between polls of a post that is still waiting to go out.</summary>
    public static readonly TimeSpan ScheduledPollInterval = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How far a provider's scheduled time may differ from ours and still be the same post.
    /// </summary>
    /// <remarks>
    /// Matching an unacknowledged post needs the channel, the text and the time, and a provider may round or
    /// shift the time it stores. Too tight a window would miss a post this server made — which would be read
    /// as "never created" — and too loose would match a different post with the same words. A minute either
    /// way is the tolerance a scheduling API's own rounding needs.
    /// </remarks>
    public static readonly TimeSpan DueAtMatchTolerance = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How long after its scheduled time a record is still polled for an outcome.
    /// </summary>
    /// <remarks>
    /// After this it becomes <see cref="PublishingRecordStatus.NeedsAttention"/> rather than being polled for
    /// ever: a provider that has said nothing for a week will not start, and an unbounded poll is a quota
    /// spent on a question nobody is waiting for.
    /// </remarks>
    public static readonly TimeSpan ReconcileWindow = TimeSpan.FromDays(7);
}

namespace CreatorPantry.Domain.Modules.Publishing.Managers;

/// <summary>The stable codes a publishing command answers with (api-contract.md).</summary>
/// <remarks>
/// None of them carries a provider's message, status code or identifier. What the provider said is recorded
/// on the ledger as a sanitized category and is read back from the record, never relayed in a refusal.
/// </remarks>
public static class PublishingErrorCodes
{
    /// <summary>
    /// The confirmation is not usable: no text or too much of it, a time that is not in the future, a missing
    /// confirmation, or a card, profile or picture that is not this workspace's. Carries field errors.
    /// </summary>
    /// <remarks>
    /// One code for "not there" and "not yours", as every other cross-module id check answers, so asking
    /// cannot reveal that a neighbour owns the id (tenancy.md).
    /// </remarks>
    public const string ScheduleInvalid = "publishing.schedule.invalid";

    /// <summary>Confirming a publication takes the Editor role.</summary>
    public const string ScheduleForbidden = "publishing.schedule.forbidden";

    /// <summary>The cancellation is not usable: no token, or one this API did not issue.</summary>
    public const string CancelInvalid = "publishing.cancel.invalid";

    /// <summary>Cancelling a publication takes the Editor role.</summary>
    public const string CancelForbidden = "publishing.cancel.forbidden";

    /// <summary>
    /// The profile is this workspace's, but the provider will not post to it — it needs reconnecting, it is
    /// locked, or the provider has stopped listing it. Well formed, so 422: the remedy is at the provider.
    /// </summary>
    public const string ProfileNotReady = "publishing.profile.not_ready.unprocessable";

    /// <summary>
    /// The platform the caller expected is not the profile's.
    /// </summary>
    /// <remarks>
    /// A conflict rather than a validation error, because nothing the caller submitted is malformed: their
    /// copy of the profile list is out of date, and the remedy is to read it again.
    /// </remarks>
    public const string PlatformMismatch = "publishing.platform.mismatch.conflict";

    /// <summary>
    /// The workspace has no scheduling time zone, so there is no way to say what local time was meant.
    /// </summary>
    /// <remarks>
    /// Refused rather than guessed: a zone chosen by the server would schedule a post for an hour the creator
    /// did not pick, and the record would then misreport their intent (publishing.md).
    /// </remarks>
    public const string TimeZoneUnset = "publishing.time_zone.unset.unprocessable";

    /// <summary>
    /// A picture cannot be published yet.
    /// </summary>
    /// <remarks>
    /// The provider fetches an image by public URL, and CreatorPantry's object store deliberately issues no
    /// addresses (media.md, <c>IPrivateObjectStore</c>). Refused rather than quietly sending the text alone,
    /// which would leave a creator believing a picture went out and a ledger row saying it did.
    /// </remarks>
    public const string MediaNotPublishable = "publishing.media.not_publishable.unprocessable";

    /// <summary>
    /// This workspace has no publishing provider.
    /// </summary>
    /// <remarks>
    /// The same answer for a deployment with no provider at all and for a workspace that is not the one the
    /// configured credential belongs to, so this cannot be used to learn that a neighbour has one.
    /// </remarks>
    public const string ProviderNotConfigured = "publishing.provider.not_configured.unprocessable";

    /// <summary>
    /// A write was asked for against a state the caller has not seen. Read the record again.
    /// </summary>
    public const string RecordConflict = "publishing.record.conflict";

    /// <summary>
    /// Whether this was published is not known, so it cannot be cancelled.
    /// </summary>
    /// <remarks>
    /// The honest refusal behind the rule that an unknown outcome is never recorded as cancelled: without the
    /// provider's id for a post there is nothing to ask it to remove, and saying "cancelled" would be a claim
    /// about a post that may well be going out. Reconciliation settles it, and the cancellation then works.
    /// </remarks>
    public const string OutcomeUnknown = "publishing.outcome_unknown.conflict";

    /// <summary>
    /// There is nothing to cancel: the publication already failed and will not go out.
    /// </summary>
    public const string NotCancellable = "publishing.not_cancellable.conflict";

    /// <summary>
    /// It has already been published. Taking something down again is a separate capability (publishing.md),
    /// and cancelling a post that is already out would be a promise this cannot keep.
    /// </summary>
    public const string AlreadyPublished = "publishing.already_published.conflict";

    /// <summary>No record in this workspace has that id. Also the answer for another workspace's.</summary>
    public const string RecordNotFound = "publishing.record.not_found";

    /// <summary>
    /// This workspace is already publishing exactly this, to that profile, at that time.
    /// </summary>
    /// <remarks>
    /// The refusal an idempotency key cannot give: two keys are two requests, so the key's unique index sees
    /// nothing wrong with the same post being confirmed twice. Carries the existing record's id as the
    /// <c>publishingRecordId</c> extension, so a client can show what is already going out. A failed or
    /// cancelled publication does not block a new one.
    /// </remarks>
    public const string DuplicatePublication = "publishing.duplicate.conflict";

    /// <summary>
    /// Two confirmations with the same key arrived at once and this one lost the race. Repeating it returns
    /// the record the winner made.
    /// </summary>
    public const string ScheduleConflict = "publishing.schedule.conflict";

    /// <summary>
    /// The confirmation could not be committed, so nothing was written and nothing was sent.
    /// </summary>
    /// <remarks>
    /// Not the caller's doing and not permanent, which is why it is a 503 rather than a validation error: the
    /// same request, with the same key, may well succeed.
    /// </remarks>
    public const string ScheduleUnavailable = "publishing.schedule.unavailable";
}

namespace CreatorPantry.Domain.Modules.Publishing.Managers;

/// <summary>
/// One publishing record as a caller reads it (PUB-002/003).
/// </summary>
/// <remarks>
/// <para>
/// <strong>No provider identifier, ever.</strong> The provider's id for the post it made is in the integration
/// record and never leaves the server, so there is no field for it here (publishing.md, external.md). Nor is
/// there a provider message: <paramref name="FailureCategory"/> is CreatorPantry's own stable word for what
/// went wrong and <paramref name="FailureSummary"/> is a sanitized line.
/// </para>
/// <para>
/// <strong><paramref name="OutcomeUncertain"/> is the field a caller must not round off.</strong> True means a
/// write to the provider was begun and its outcome is not known — a post may exist. A screen that renders it
/// as either "scheduled" or "failed" would be claiming something nobody knows (13.10d).
/// </para>
/// </remarks>
/// <param name="Id">CreatorPantry's own id for the record, and the one the cancel command names.</param>
/// <param name="ContentCardId">The card it was confirmed from, or null once that card has been deleted.</param>
/// <param name="PublishingProfileId">CreatorPantry's own id for the profile.</param>
/// <param name="Platform">The network, as recorded at confirmation.</param>
/// <param name="Text">The text as confirmed, which is what was sent. The creator's own words.</param>
/// <param name="ScheduledAt">The instant it is to be published (UTC).</param>
/// <param name="ScheduleTimeZoneId">The IANA zone the creator scheduled in.</param>
/// <param name="ScheduledLocal">
/// The local wall-clock time they chose, with no offset: their intent, beside the instant it resolved to.
/// </param>
/// <param name="SubmittedAt">When the provider accepted it, or null while none has.</param>
/// <param name="SubmitAttempts">How many times the provider has been asked to take this post.</param>
/// <param name="ConfirmedAt">When the creator confirmed it.</param>
/// <param name="ConcurrencyToken">What a cancel quotes back.</param>
public sealed record PublishingRecordServiceModel(
    Guid Id,
    Guid? ContentCardId,
    Guid PublishingProfileId,
    PublishingPlatform Platform,
    PublishingRecordStatus Status,
    string Text,
    Guid? MediaAssetId,
    int? MediaAssetVersionNumber,
    DateTimeOffset ScheduledAt,
    string ScheduleTimeZoneId,
    DateTime ScheduledLocal,
    bool OutcomeUncertain,
    string? FailureCategory,
    string? FailureSummary,
    DateTimeOffset? SubmittedAt,
    DateTimeOffset? CompletedAt,
    int SubmitAttempts,
    DateTimeOffset ConfirmedAt,
    string ConcurrencyToken);

/// <summary>
/// One card's publications, newest scheduled first.
/// </summary>
/// <remarks>
/// Capped rather than paged (<see cref="PublishingLedgerPolicy.RecordsPerCard"/>), and
/// <paramref name="Complete"/> says whether the cap was reached — so a client is never shown a partial list
/// believing it is the whole one.
/// </remarks>
public sealed record PublishingRecordListServiceModel(
    IReadOnlyList<PublishingRecordServiceModel> Items, bool Complete);

/// <summary>
/// What a confirmation produced: the record, and whether this call made it.
/// </summary>
/// <remarks>
/// Declared here rather than beside the Business interface that returns it, because a facade's signature is
/// part of what crosses a module boundary and only <c>Managers</c> and <c>Facade</c> types may (backend.md).
/// </remarks>
/// <param name="Replayed">
/// True when that idempotency key already named this record and nothing was sent to the provider again.
/// </param>
public sealed record PublishingScheduleOutcome(PublishingRecordServiceModel Record, bool Replayed);

/// <summary>A publishing record's row version as the opaque token a client quotes back on a cancel.</summary>
public static class PublishingRecordConcurrencyToken
{
    public static string From(byte[] rowVersion) => Convert.ToBase64String(rowVersion);

    /// <summary>True for a token this API could have issued, whatever record it was issued for.</summary>
    public static bool IsWellFormed(string? token) =>
        !string.IsNullOrWhiteSpace(token) && Try(token, out var bytes) && bytes.Length is 8;

    /// <summary>True when <paramref name="token"/> is the token of exactly this row version.</summary>
    public static bool Matches(string? token, byte[] rowVersion) =>
        !string.IsNullOrWhiteSpace(token) && Try(token, out var bytes) && bytes.AsSpan().SequenceEqual(rowVersion);

    private static bool Try(string token, out byte[] bytes)
    {
        bytes = [];
        var buffer = new byte[12];

        if (!Convert.TryFromBase64String(token, buffer, out var written))
        {
            return false;
        }

        bytes = buffer[..written];

        return true;
    }
}

using CreatorPantry.Domain.Modules.Publishing.Managers;

namespace CreatorPantry.Domain.Modules.Publishing.Gateways;

/// <summary>
/// One post to hand to the provider, in the application's own terms (INT-031).
/// </summary>
/// <remarks>
/// The provider's own id for the channel is the only external value here, read from the integration record by
/// the data layer. Nothing above the data layer ever holds one (external.md).
/// </remarks>
/// <param name="ChannelExternalId">The provider's id for the profile being posted to.</param>
/// <param name="Text">The text to publish, as confirmed.</param>
/// <param name="DueAt">The instant it should be published, in UTC.</param>
public sealed record PublishingSubmission(string ChannelExternalId, string Text, DateTimeOffset DueAt);

/// <summary>How one submit of one post ended.</summary>
public enum PublishingSubmissionOutcome
{
    /// <summary>The provider took the post and will publish it. Its id for the post is on the result.</summary>
    Scheduled = 1,

    /// <summary>
    /// The provider answered and said no: it would not take this post.
    /// </summary>
    /// <remarks>
    /// Used only for refusals whose own nature precedes creating anything — a credential it rejected, input
    /// it would not accept, an account limit. A refusal this server cannot place in that category is
    /// <see cref="Unknown"/>, because a terminal state is a claim that nothing was published.
    /// </remarks>
    Refused = 2,

    /// <summary>Throttled before it acted. Safe to repeat, after the wait.</summary>
    RateLimited = 3,

    /// <summary>
    /// The provider could not have acted, and a gateway can prove it.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="Unknown"/> on purpose, and the distinction is the whole value of this enum: a
    /// request that provably never reached the provider may be sent again. The bar is <em>proof</em>, which is
    /// high — the Buffer adapter never returns this, because nothing Buffer documents (not a 5xx, not
    /// <c>UNEXPECTED</c>, not a dropped connection) establishes that the mutation did not run. A throttle is
    /// reported as <see cref="RateLimited"/> instead, which is the one failure Buffer does document as
    /// happening in place of acting.
    /// </remarks>
    Unavailable = 4,

    /// <summary>
    /// Nothing usable came back, and the post may or may not exist at the provider.
    /// </summary>
    /// <remarks>
    /// A timeout, a dropped connection mid-request, or an answer this contract cannot read. The post must
    /// <strong>not</strong> be submitted again on the strength of this: reconciliation asks the provider what
    /// actually happened (publishing.md, 13.10c).
    /// </remarks>
    Unknown = 5,

    /// <summary>This host has no publishing provider. Nothing was sent.</summary>
    NotConfigured = 6,
}

/// <param name="ExternalUpdateId">
/// The provider's id for the post it created, present exactly when the outcome is
/// <see cref="PublishingSubmissionOutcome.Scheduled"/>. It goes to the integration record and nowhere else.
/// </param>
/// <param name="RetryAfter">How long the provider asked to be left alone, when it said.</param>
/// <param name="FailureCategory">
/// CreatorPantry's own stable word for what went wrong, derived from the provider's error <em>type</em> — never
/// from its message, which can quote the post back.
/// </param>
/// <param name="FailureSummary">A sentence of this server's own writing. Never the provider's.</param>
/// <param name="ProviderStatusCode">The HTTP status, when there was one, for the attempt record.</param>
public sealed record PublishingSubmissionResult(
    PublishingSubmissionOutcome Outcome,
    string? ExternalUpdateId = null,
    TimeSpan? RetryAfter = null,
    string? FailureCategory = null,
    string? FailureSummary = null,
    int? ProviderStatusCode = null)
{
    public static PublishingSubmissionResult Of(
        PublishingSubmissionOutcome outcome,
        string? failureCategory = null,
        string? failureSummary = null,
        int? providerStatusCode = null,
        TimeSpan? retryAfter = null) =>
        new(outcome, null, retryAfter, failureCategory, failureSummary, providerStatusCode);
}

/// <summary>
/// One post to ask the provider to remove (INT-032).
/// </summary>
/// <param name="ExternalUpdateId">
/// The provider's id for the post, read from the integration record. A cancellation cannot be asked for
/// without one, which is why a record whose submit outcome is unknown is never cancelled: there is no post to
/// name (publishing.md).
/// </param>
public sealed record PublishingCancellation(string ExternalUpdateId);

/// <summary>How one attempt to remove one post ended.</summary>
public enum PublishingCancellationOutcome
{
    /// <summary>The provider removed it.</summary>
    Cancelled = 1,

    /// <summary>
    /// The provider has no such post. Treated as success: not being there is what was asked for.
    /// </summary>
    /// <remarks>
    /// Also what a repeated cancellation answers, which is what makes repeating one safe — and the reason a
    /// cancellation may be retried where a submit may not.
    /// </remarks>
    AlreadyGone = 2,

    /// <summary>
    /// The provider will not remove it — typically because it has already gone out.
    /// </summary>
    /// <remarks>The post still stands, so the record goes back to saying so rather than to cancelled.</remarks>
    NotDeletable = 3,

    /// <summary>The provider refused the credential, or says it may not do this. The post still stands.</summary>
    Refused = 4,

    /// <summary>Throttled before it acted. The post still stands and asking again is safe.</summary>
    RateLimited = 5,

    /// <summary>
    /// Nothing usable came back, so whether the post is gone is not known.
    /// </summary>
    /// <remarks>
    /// The record must not be recorded as cancelled on the strength of this. Asking again is safe and is how
    /// it settles: a post that was removed answers <see cref="AlreadyGone"/> next time.
    /// </remarks>
    Unknown = 6,

    /// <summary>This host has no publishing provider. Nothing was asked.</summary>
    NotConfigured = 7,
}

/// <inheritdoc cref="PublishingSubmissionResult"/>
public sealed record PublishingCancellationResult(
    PublishingCancellationOutcome Outcome,
    TimeSpan? RetryAfter = null,
    string? FailureCategory = null,
    string? FailureSummary = null,
    int? ProviderStatusCode = null);

/// <summary>
/// A request to list what the provider is holding for one channel (INT-033).
/// </summary>
/// <param name="ExternalAccountId">The provider's id for the account that holds the channel.</param>
/// <param name="ChannelExternalId">The provider's id for the channel.</param>
public sealed record PublishingPostLookup(string ExternalAccountId, string ChannelExternalId);

/// <summary>What the provider says has become of one of its posts.</summary>
public enum PublishingPostState
{
    /// <summary>It is still to go out.</summary>
    Scheduled = 1,

    /// <summary>It went out.</summary>
    Sent = 2,

    /// <summary>The provider tried and could not.</summary>
    Failed = 3,
}

/// <summary>One post the provider is holding, in the application's own terms.</summary>
/// <param name="ExternalUpdateId">
/// The provider's id for it. The one provider value this carries, and it travels no further than the
/// integration record (external.md).
/// </param>
/// <param name="Text">Its text, for matching a post this server may have created and not heard about.</param>
/// <param name="DueAt">When the provider has it scheduled for, when it said.</param>
public sealed record ProviderPost(
    string ExternalUpdateId, string Text, DateTimeOffset? DueAt, PublishingPostState State);

/// <summary>How one listing ended.</summary>
public enum PublishingPostLookupOutcome
{
    /// <summary>The provider answered. What it holds is on the result.</summary>
    Listed = 1,

    /// <summary>Throttled. Nothing may be concluded from this.</summary>
    RateLimited = 2,

    /// <summary>Could not be read. Nothing may be concluded from this either.</summary>
    Unavailable = 3,

    /// <summary>This host has no publishing provider.</summary>
    NotConfigured = 4,
}

/// <param name="Posts">What the provider holds for that channel. Empty unless the outcome is
/// <see cref="PublishingPostLookupOutcome.Listed"/>.</param>
/// <param name="Complete">
/// True when the listing reached the end of what the provider holds.
/// </param>
/// <remarks>
/// <para>
/// <strong><paramref name="Complete"/> is the load-bearing field, and the reason this type exists rather than
/// a bare list.</strong> Concluding that a post was never created — which is the one conclusion that can lose
/// a creator's work — is only honest from a listing that was read to its end. A listing cut short by the page
/// cap proves nothing about what lies past it, and a read that failed proves nothing at all.
/// </para>
/// </remarks>
public sealed record PublishingPostLookupResult(
    PublishingPostLookupOutcome Outcome,
    IReadOnlyList<ProviderPost> Posts,
    bool Complete = false,
    TimeSpan? RetryAfter = null)
{
    public static PublishingPostLookupResult Of(
        PublishingPostLookupOutcome outcome, TimeSpan? retryAfter = null) => new(outcome, [], false, retryAfter);
}

/// <summary>
/// The boundary to a publishing provider's post commands (PUB-002/003, INT-031/032/033).
/// </summary>
/// <remarks>
/// <para>
/// <strong>It sends exactly once.</strong> Creating a post is not safe to repeat and the provider offers no
/// idempotency key of its own, so no retry happens here, in the HTTP handler, or anywhere else below the
/// ledger. An attempt that cannot be accounted for is <see cref="PublishingSubmissionOutcome.Unknown"/>, and
/// what to do about that is a decision with a durable record behind it.
/// </para>
/// <para>
/// <strong>It never throws for a provider failure.</strong> A caller cancelling is the one exception that
/// propagates. Every other way the call can end is an outcome.
/// </para>
/// </remarks>
public interface IPublishingPostGateway
{
    /// <summary>Which provider this host is configured for, and whose it is.</summary>
    PublishingProviderDescriptor Describe();

    /// <summary>Asks the provider to publish one post at one time. One attempt, no retry.</summary>
    Task<PublishingSubmissionResult> SubmitAsync(PublishingSubmission submission, CancellationToken cancellationToken);

    /// <summary>
    /// Asks the provider to remove one post it is holding.
    /// </summary>
    /// <remarks>
    /// <strong>Unlike a submit, this may be retried.</strong> Asking twice cannot remove two posts: the second
    /// ask answers <see cref="PublishingCancellationOutcome.AlreadyGone"/>, which is the outcome that was
    /// wanted. So an implementation may retry a transient failure within bounds, and every attempt can only
    /// move the record closer to certainty.
    /// </remarks>
    Task<PublishingCancellationResult> CancelAsync(
        PublishingCancellation cancellation, CancellationToken cancellationToken);

    /// <summary>
    /// Lists what the provider is holding for one channel, so a record can be told what became of its post.
    /// </summary>
    /// <remarks>
    /// Read-only, so an implementation may retry freely. One listing settles every record waiting on that
    /// channel, which is what keeps reconciliation inside a small quota.
    /// </remarks>
    Task<PublishingPostLookupResult> ListPostsAsync(
        PublishingPostLookup lookup, CancellationToken cancellationToken);
}

/// <summary>
/// The post gateway on a host with no publishing provider: it says so, and calls nothing.
/// </summary>
/// <remarks>
/// Registered by default beside <see cref="UnconfiguredPublishingProfileGateway"/>, so a clean clone starts
/// and a confirmation is refused with a stable answer rather than a host that will not boot (external.md).
/// </remarks>
public sealed class UnconfiguredPublishingPostGateway : IPublishingPostGateway
{
    public PublishingProviderDescriptor Describe() => PublishingProviderDescriptor.None;

    public Task<PublishingSubmissionResult> SubmitAsync(
        PublishingSubmission submission, CancellationToken cancellationToken) =>
        Task.FromResult(PublishingSubmissionResult.Of(PublishingSubmissionOutcome.NotConfigured));

    public Task<PublishingCancellationResult> CancelAsync(
        PublishingCancellation cancellation, CancellationToken cancellationToken) =>
        Task.FromResult(new PublishingCancellationResult(PublishingCancellationOutcome.NotConfigured));

    public Task<PublishingPostLookupResult> ListPostsAsync(
        PublishingPostLookup lookup, CancellationToken cancellationToken) =>
        Task.FromResult(PublishingPostLookupResult.Of(PublishingPostLookupOutcome.NotConfigured));
}

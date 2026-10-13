using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using CreatorPantry.Domain.Modules.Publishing.Gateways;
using CreatorPantry.Domain.Modules.Publishing.Managers;
using Microsoft.Extensions.Logging;

namespace CreatorPantry.Domain.Modules.Publishing.Integration.Buffer;

/// <summary>
/// Buffer, as the thing that takes one scheduled post (PUB-002, INT-031).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The contract this is written against</strong> is Buffer's GraphQL API at
/// developers.buffer.com: <c>createPost(input: CreatePostInput!): PostActionPayload!</c>, with
/// <c>schedulingType: automatic</c> (Buffer's own workers publish it) and <c>mode: customScheduled</c>
/// (publish at <c>dueAt</c> rather than at the next queue slot). <c>dueAt</c> is the <c>DateTime</c> scalar,
/// ISO 8601; it is sent in UTC with milliseconds. The payload is a union: <c>PostActionSuccess</c> carries the
/// post, and the error members all carry a <c>message</c>.
/// </para>
/// <para>
/// <strong>One attempt. No retry, anywhere.</strong> Buffer offers no idempotency key, so nothing can tell a
/// repeated <c>createPost</c> apart from a second post somebody wanted — not this gateway, not the HTTP
/// handler (the host removes the resilience handler for exactly this reason), and not the caller. An attempt
/// that cannot be accounted for comes back <see cref="PublishingSubmissionOutcome.Unknown"/>.
/// </para>
/// <para>
/// <strong>A refusal's <em>type</em> crosses the boundary; its message does not.</strong> Buffer's error
/// message is written by Buffer and can quote the post text — and therefore the creator's own words — back at
/// us, so it is neither stored nor logged nor returned. What crosses is the error's GraphQL type name, which
/// is a schema symbol, mapped to one of this server's own categories, and a sentence written here
/// (external.md, publishing.md).
/// </para>
/// <para>
/// <strong>Nothing of Buffer's is logged but a shape:</strong> the outcome, the HTTP status and the error
/// category. Never the key, a body, a post id or a message.
/// </para>
/// </remarks>
public sealed class BufferPublishingPostGateway : IPublishingPostGateway
{
    /// <summary>
    /// <c>__typename</c> is selected so a refusal can be categorised by type rather than by reading its
    /// English. Only the post's id is read back: its text is ours already, and its assets are not used yet.
    /// </summary>
    private const string CreatePostMutation =
        "mutation($input: CreatePostInput!) { createPost(input: $input) { __typename "
        + "... on PostActionSuccess { post { id dueAt } } "
        + "... on MutationError { __typename } } }";

    /// <summary>
    /// <c>__typename</c> and nothing else, which is valid against any union.
    /// </summary>
    /// <remarks>
    /// Deliberately field-free. <c>DeletePostPayload</c>'s members are not in the part of Buffer's reference
    /// this was written against, and naming a member a union does not have makes the whole mutation a
    /// validation error — which would turn every cancellation into an unknown outcome. A type name is enough
    /// to tell "it is gone" from "it will not be removed", and it is a schema symbol rather than prose.
    /// </remarks>
    private const string DeletePostMutation =
        "mutation($input: DeletePostInput!) { deletePost(input: $input) { __typename } }";

    /// <summary>How many times a removal is asked for before the outcome is left unknown.</summary>
    /// <remarks>
    /// Where <c>createPost</c> gets one attempt and no more, this gets three: a repeated removal cannot remove
    /// a second post, so retrying only narrows the window in which nobody knows whether the post is gone.
    /// </remarks>
    private const int MaxCancelAttempts = 3;

    /// <summary>
    /// Buffer's own shape for "what are you holding for this channel" (INT-033).
    /// </summary>
    /// <remarks>
    /// <c>posts(input: PostsInput!)</c> with an organization, a status filter and a channel filter, as
    /// developers.buffer.com documents it. <c>dueAt</c> comes back but is not a documented filter, so the
    /// matching on time happens here. All three statuses are asked for at once: one listing then settles a
    /// record waiting to hear it was sent, one waiting to hear it exists, and one waiting to hear it is gone.
    /// </remarks>
    private const string PostsQuery =
        "query($input: PostsInput!, $first: Int, $after: String) "
        + "{ posts(input: $input, first: $first, after: $after) "
        + "{ edges { node { id text dueAt channelId status } } pageInfo { hasNextPage endCursor } } }";

    /// <summary>How many posts one listing reads before it stops claiming to be complete.</summary>
    private const int PostsPerPage = 50;

    /// <summary>The most pages one listing will read. Past this it is incomplete, and says so.</summary>
    private const int MaxPostPages = 4;

    /// <summary>How many times a listing is asked for before it is reported unavailable. Read-only, so free to repeat.</summary>
    private const int MaxLookupAttempts = 3;

    private static readonly TimeSpan LongestThrottleWorthWaitingFor = TimeSpan.FromSeconds(2);

    private readonly BufferGraphQl _graphQl;
    private readonly BufferPublishingOptions _options;
    private readonly ILogger<BufferPublishingPostGateway> _logger;

    public BufferPublishingPostGateway(
        HttpClient http, BufferPublishingOptions options, ILogger<BufferPublishingPostGateway> logger)
    {
        _graphQl = new BufferGraphQl(http, options, logger);
        _options = options;
        _logger = logger;
    }

    public PublishingProviderDescriptor Describe() =>
        new(true, PublishingPolicy.BufferProviderKey, _options.WorkspaceId);

    public async Task<PublishingSubmissionResult> SubmitAsync(
        PublishingSubmission submission, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(submission);

        var started = Stopwatch.GetTimestamp();
        var result = await SubmitCoreAsync(submission, cancellationToken);

        // The whole of what is recorded about a submit: how it ended, which category, how long.
        _logger.LogInformation(
            "Buffer post submit ended {Outcome} (category {Category}) in {ElapsedMs} ms.",
            result.Outcome,
            result.FailureCategory ?? "none",
            (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);

        return result;
    }

    private async Task<PublishingSubmissionResult> SubmitCoreAsync(
        PublishingSubmission submission, CancellationToken cancellationToken)
    {
        // Every value travels as a variable; nothing is spliced into the document. The creator's text is the
        // reason that matters — a post body is exactly the kind of content that would otherwise be able to
        // close the input object and add fields of its own.
        var variables = new
        {
            input = new
            {
                channelId = submission.ChannelExternalId,
                text = submission.Text,
                schedulingType = "automatic",
                mode = "customScheduled",

                // Buffer's documented shape, to the millisecond, in UTC.
                dueAt = submission.DueAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
            },
        };

        var reply = await _graphQl.SendOnceAsync(CreatePostMutation, variables, cancellationToken);

        // The success is read first, whatever else came with it. Buffer documents that a successful mutation
        // can answer with errors beside its data, and a post that was made and warned about must not be
        // recorded as a post that was never made.
        if (TryReadPostId(reply.Data, out var postId, out var refusal))
        {
            return new PublishingSubmissionResult(
                PublishingSubmissionOutcome.Scheduled, postId, ProviderStatusCode: reply.Status);
        }

        return reply.Kind switch
        {
            // Throttled: the one failure Buffer documents as happening instead of acting, so it is the one
            // that may be sent again.
            BufferReplyKind.Throttled => PublishingSubmissionResult.Of(
                PublishingSubmissionOutcome.RateLimited,
                "rate_limited",
                "The publishing provider is rate limiting this deployment.",
                reply.Status,
                reply.RetryAfter),

            // A credential that was refused cannot have posted anything, and another attempt changes nothing.
            BufferReplyKind.Refused => PublishingSubmissionResult.Of(
                PublishingSubmissionOutcome.Refused,
                "credential_rejected",
                "The publishing provider refused this deployment's credential.",
                reply.Status),

            // A refusal Buffer named in its payload: a limit, or input it will not take.
            BufferReplyKind.Data when refusal is not null => PublishingSubmissionResult.Of(
                PublishingSubmissionOutcome.Refused,
                refusal,
                "The publishing provider would not accept this post.",
                reply.Status),

            // Everything else is unknown, and deliberately so. A timeout, a dropped connection, a 5xx, an
            // UNEXPECTED, a payload member this was not written against: Buffer documents none of them as
            // meaning the mutation did not run, and its guide advises retrying UNEXPECTED — which for
            // createPost is the duplicate-post path. So nothing here is read as "nothing was published".
            _ => PublishingSubmissionResult.Of(
                PublishingSubmissionOutcome.Unknown,
                CategoryOf(reply),
                "The publishing provider did not confirm this post, so whether it was published is not yet known.",
                reply.Status),
        };
    }

    /// <summary>What to call an answer that settled nothing, from the shape of the reply alone.</summary>
    private static string CategoryOf(BufferReply reply) => reply.Code switch
    {
        "timeout" => "provider_timeout",
        "connection" => "provider_connection_lost",
        "UNEXPECTED" => "provider_unexpected",
        _ => reply.Status >= 500 ? "provider_server_error" : "unreadable_answer",
    };

    /// <summary>
    /// The created post's id, or the category of the refusal that came instead.
    /// </summary>
    /// <remarks>
    /// An id longer than the integration record can hold is treated as unreadable rather than truncated: half
    /// an id maps to nothing, and a mapping record that cannot name the post is worse than none.
    /// </remarks>
    private static bool TryReadPostId(JsonElement data, out string? postId, out string? refusalCategory)
    {
        postId = null;
        refusalCategory = null;

        // A reply that carried no data at all — a throttle, a refused credential, a timeout — has an undefined
        // element here, which cannot be read as an object at all.
        if (data.ValueKind is not JsonValueKind.Object)
        {
            return false;
        }

        if (!data.TryGetProperty("createPost", out var payload) || payload.ValueKind is not JsonValueKind.Object)
        {
            return false;
        }

        var typeName = Text(payload, "__typename");
        var hasPost = payload.TryGetProperty("post", out var post) && post.ValueKind is JsonValueKind.Object;

        if (typeName is "PostActionSuccess" || hasPost)
        {
            // It says the post was created. If its id cannot be read — missing, or longer than the mapping
            // record holds — the honest answer is that a post exists and this server cannot name it, which
            // leaves the outcome unknown. Categorising it as a refusal would lose a real post, and half an id
            // maps to nothing.
            if (hasPost
                && Text(post, "id") is { Length: > 0 } id
                && id.Length <= PublishingPolicy.ExternalIdMaxLength)
            {
                postId = id;

                return true;
            }

            return false;
        }

        // Every other member of the payload union is a refusal. Categorised by type name — a schema symbol,
        // not creator content — and by nothing the provider wrote in prose.
        // Only the error types this was written against are read as definite refusals. Anything else —
        // a member Buffer adds later, or a payload with no type name at all — leaves the outcome unknown,
        // because declaring a post failed is a claim this cannot support from a symbol it does not know.
        refusalCategory = typeName switch
        {
            "LimitReachedError" => "provider_limit_reached",
            "InvalidInputError" => "provider_rejected_input",
            "MutationError" => "provider_refused",
            _ => null,
        };

        return false;
    }

    public async Task<PublishingCancellationResult> CancelAsync(
        PublishingCancellation cancellation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cancellation);

        var started = Stopwatch.GetTimestamp();
        var result = await CancelCoreAsync(cancellation, cancellationToken);

        _logger.LogInformation(
            "Buffer post cancellation ended {Outcome} (category {Category}) in {ElapsedMs} ms.",
            result.Outcome,
            result.FailureCategory ?? "none",
            (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);

        return result;
    }

    private async Task<PublishingCancellationResult> CancelCoreAsync(
        PublishingCancellation cancellation, CancellationToken cancellationToken)
    {
        // The id travels as a variable, never spliced into the document.
        var variables = new { input = new { id = cancellation.ExternalUpdateId } };
        var waitedForThrottle = false;

        for (var attempt = 1; ; attempt++)
        {
            var reply = await _graphQl.SendOnceAsync(DeletePostMutation, variables, cancellationToken);

            // A post the provider does not have is a post that is not going out, which is what was asked for.
            if (reply.Code is "NOT_FOUND")
            {
                return new PublishingCancellationResult(
                    PublishingCancellationOutcome.AlreadyGone,
                    FailureCategory: "provider_post_absent",
                    FailureSummary: "The publishing provider no longer holds that post.",
                    ProviderStatusCode: reply.Status);
            }

            // Read before the kind, as the submit path does: a removal that worked and warned about
            // something must not be read as a removal that did not happen.
            if (TypeNameOf(reply.Data) is { } typeName)
            {
                if (typeName.Contains("Success", StringComparison.Ordinal))
                {
                    return new PublishingCancellationResult(
                        PublishingCancellationOutcome.Cancelled, ProviderStatusCode: reply.Status);
                }

                if (typeName is "PostNotDeletableError")
                {
                    return new PublishingCancellationResult(
                        PublishingCancellationOutcome.NotDeletable,
                        FailureCategory: "provider_post_not_deletable",
                        FailureSummary: "The publishing provider will not remove that post.",
                        ProviderStatusCode: reply.Status);
                }
            }

            if (reply.Kind is BufferReplyKind.Refused)
            {
                return new PublishingCancellationResult(
                    PublishingCancellationOutcome.Refused,
                    FailureCategory: "credential_rejected",
                    FailureSummary: "The publishing provider refused this deployment's credential.",
                    ProviderStatusCode: reply.Status);
            }

            if (reply.Kind is BufferReplyKind.Throttled)
            {
                // Waited out once when the provider asks for a moment; otherwise reported, because holding a
                // request open for a quota window helps nobody.
                if (!waitedForThrottle && reply.RetryAfter is { } wait && wait <= LongestThrottleWorthWaitingFor)
                {
                    waitedForThrottle = true;
                    await Task.Delay(wait, cancellationToken);
                    continue;
                }

                return new PublishingCancellationResult(
                    PublishingCancellationOutcome.RateLimited,
                    reply.RetryAfter,
                    "rate_limited",
                    "The publishing provider is rate limiting this deployment.",
                    reply.Status);
            }

            // Everything left is a silence or an answer this cannot read. Safe to ask again, and worth it.
            if (attempt < MaxCancelAttempts)
            {
                await Task.Delay(_options.RetryBaseDelay * attempt, cancellationToken);
                continue;
            }

            return new PublishingCancellationResult(
                PublishingCancellationOutcome.Unknown,
                FailureCategory: CategoryOf(reply),
                FailureSummary: "The publishing provider did not confirm the removal, so whether that post is "
                    + "still going out is not yet known.",
                ProviderStatusCode: reply.Status);
        }
    }

    public async Task<PublishingPostLookupResult> ListPostsAsync(
        PublishingPostLookup lookup, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lookup);

        var started = Stopwatch.GetTimestamp();
        var result = await ListPostsCoreAsync(lookup, cancellationToken);

        _logger.LogInformation(
            "Buffer post listing ended {Outcome} with {PostCount} post(s), complete {Complete}, in {ElapsedMs} ms.",
            result.Outcome,
            result.Posts.Count,
            result.Complete,
            (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);

        return result;
    }

    private async Task<PublishingPostLookupResult> ListPostsCoreAsync(
        PublishingPostLookup lookup, CancellationToken cancellationToken)
    {
        var posts = new List<ProviderPost>();
        string? after = null;

        for (var page = 1; page <= MaxPostPages; page++)
        {
            var variables = new
            {
                input = new
                {
                    organizationId = lookup.ExternalAccountId,
                    // The channel, and deliberately no status filter. Asking for a set of statuses means a
                    // post in any state outside it reads as absent — and an absence is what licenses
                    // recording a live post as never made. Every status comes back and is read per post.
                    filter = new { channelIds = new[] { lookup.ChannelExternalId } },
                },
                first = PostsPerPage,
                after,
            };

            var reply = await SendWithRetryAsync(variables, cancellationToken);

            if (reply.Kind is not BufferReplyKind.Data)
            {
                // Nothing may be concluded from a listing that did not happen — least of all that a post is
                // absent.
                return reply.Kind is BufferReplyKind.Throttled
                    ? PublishingPostLookupResult.Of(PublishingPostLookupOutcome.RateLimited, reply.RetryAfter)
                    : PublishingPostLookupResult.Of(PublishingPostLookupOutcome.Unavailable);
            }

            if (!TryReadPosts(reply.Data, posts, out var hasNextPage, out var endCursor))
            {
                return PublishingPostLookupResult.Of(PublishingPostLookupOutcome.Unavailable);
            }

            if (!hasNextPage)
            {
                // Read to the end, which is the only state from which an absence means anything.
                return new PublishingPostLookupResult(PublishingPostLookupOutcome.Listed, posts, Complete: true);
            }

            after = endCursor;

            if (endCursor is null)
            {
                break;
            }
        }

        // More than this listing reads. What was read is real; what lies past it is unknown.
        return new PublishingPostLookupResult(PublishingPostLookupOutcome.Listed, posts, Complete: false);
    }

    /// <summary>A read, so repeating it is free of consequence and worth doing.</summary>
    private async Task<BufferReply> SendWithRetryAsync(object variables, CancellationToken cancellationToken)
    {
        var waitedForThrottle = false;

        for (var attempt = 1; ; attempt++)
        {
            var reply = await _graphQl.SendOnceAsync(PostsQuery, variables, cancellationToken);

            if (reply.Kind is BufferReplyKind.Data)
            {
                return reply;
            }

            if (reply.Kind is BufferReplyKind.Throttled)
            {
                if (!waitedForThrottle && reply.RetryAfter is { } wait && wait <= LongestThrottleWorthWaitingFor)
                {
                    waitedForThrottle = true;
                    await Task.Delay(wait, cancellationToken);
                    continue;
                }

                return reply;
            }

            if (reply.Kind is BufferReplyKind.Transient && attempt < MaxLookupAttempts)
            {
                await Task.Delay(_options.RetryBaseDelay * attempt, cancellationToken);
                continue;
            }

            return reply;
        }
    }

    private static bool TryReadPosts(
        JsonElement data, List<ProviderPost> into, out bool hasNextPage, out string? endCursor)
    {
        hasNextPage = false;
        endCursor = null;

        if (!data.TryGetProperty("posts", out var posts)
            || posts.ValueKind is not JsonValueKind.Object
            || !posts.TryGetProperty("edges", out var edges)
            || edges.ValueKind is not JsonValueKind.Array)
        {
            return false;
        }

        foreach (var edge in edges.EnumerateArray())
        {
            if (!edge.TryGetProperty("node", out var node) || node.ValueKind is not JsonValueKind.Object)
            {
                return false;
            }

            // A post with no id cannot be matched to anything, and one too long for the mapping record is not
            // one this server could have made.
            if (Text(node, "id") is not { Length: <= PublishingPolicy.ExternalIdMaxLength } id)
            {
                return false;
            }

            into.Add(new ProviderPost(
                id,
                Text(node, "text") ?? string.Empty,
                DateTimeOffset.TryParse(
                    Text(node, "dueAt"),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                    out var dueAt)
                    ? dueAt
                    : null,
                StateOf(Text(node, "status"))));
        }

        // Completeness is the only thing that licenses concluding a post was never made, so it is read
        // rather than assumed: a reply with no readable pageInfo is unreadable, not complete. Defaulting
        // hasNextPage to false here would turn a malformed answer into proof of an absence.
        if (!posts.TryGetProperty("pageInfo", out var pageInfo)
            || pageInfo.ValueKind is not JsonValueKind.Object
            || !pageInfo.TryGetProperty("hasNextPage", out var more)
            || more.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        hasNextPage = more.ValueKind is JsonValueKind.True;
        endCursor = Text(pageInfo, "endCursor");

        return true;
    }

    /// <summary>Buffer's word for where a post stands, as this server's. One it does not know is scheduled.</summary>
    /// <remarks>
    /// The default is deliberate and conservative: an unrecognised status must not be read as "it went out" or
    /// "it failed", both of which are terminal here. Treating it as still to go out leaves the record polling.
    /// </remarks>
    internal static PublishingPostState StateOf(string? status) => status?.ToLowerInvariant() switch
    {
        "sent" => PublishingPostState.Sent,
        "error" => PublishingPostState.Failed,
        _ => PublishingPostState.Scheduled,
    };

    /// <summary>The payload's type name, when it answered with one.</summary>
    private static string? TypeNameOf(JsonElement data) =>
        data.ValueKind is JsonValueKind.Object
        && data.TryGetProperty("deletePost", out var payload)
            ? Text(payload, "__typename")
            : null;

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind is JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind is JsonValueKind.String
        && value.GetString() is { } text
        && !string.IsNullOrWhiteSpace(text)
            ? text.Trim()
            : null;
}

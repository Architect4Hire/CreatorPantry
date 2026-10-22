using Microsoft.AspNetCore.Http;

namespace CreatorPantry.ServiceDefaults;

/// <summary>
/// The <c>retry</c> extension every problem carries: what a client should do before sending the request again.
/// </summary>
/// <remarks>
/// <para>
/// One field rather than a <c>retryable</c> flag, because the 409s alone have three different remedies and a
/// boolean can only name two of them. <c>media.asset.version_taken.conflict</c> and
/// <c>media.asset.stale.conflict</c> share a status and differ only in this: the first is sent again as it
/// stands, the second only after a fresh read.
/// </para>
/// <para>
/// It is advice about recovery and never a disclosure: every value is decided by the error code or the status
/// alone, so it cannot say anything about a record the code itself does not already say.
/// </para>
/// </remarks>
public static class ProblemRetry
{
    public const string Extension = "retry";

    /// <summary>Sending it again will not end differently. Change the request, or stop.</summary>
    public const string None = "none";

    /// <summary>The caller's copy is stale. Read the resource again, then decide and send against the new state.</summary>
    public const string Reread = "reread";

    /// <summary>Nothing was written and the race was lost. The same request, unchanged, may succeed.</summary>
    public const string Same = "same";

    /// <summary>A dependency or allowance is temporarily unavailable. Wait (until <c>Retry-After</c>, when sent), then retry.</summary>
    public const string Later = "later";

    /// <summary>Every value, in the order the OpenAPI document lists them.</summary>
    public static IReadOnlyList<string> All { get; } = [None, Reread, Same, Later];

    /// <summary>
    /// The advice a status implies when no error code says more: wait for rate limits and unavailable
    /// dependencies, and otherwise do not retry.
    /// </summary>
    /// <remarks>
    /// A 500 is deliberately <see cref="None"/>: it is a fault nobody has classified, and telling every client to
    /// repeat a request that may have crashed half-way is how one bug becomes a retry storm.
    /// </remarks>
    public static string ForStatus(int status) => status switch
    {
        StatusCodes.Status429TooManyRequests
            or StatusCodes.Status502BadGateway
            or StatusCodes.Status503ServiceUnavailable
            or StatusCodes.Status504GatewayTimeout => Later,
        _ => None,
    };
}

namespace CreatorPantry.Domain.Modules.Media.Managers;

/// <summary>
/// Where one image-generation request has got to. Stored as its number; append, never renumber.
/// </summary>
/// <remarks>
/// Deliberately not <c>AiOperationStatus</c>. That enum governs a text completion through
/// <c>IAiCompletionGateway</c> and carries an acceptance vocabulary — Proposed, Accepted, PartiallyAccepted,
/// Rejected — that describes a creator judging a proposal. An image generation produces files a creator
/// chooses between, and the choosing is recorded per image in <see cref="GeneratedImageStatus"/>; sharing one
/// enum would force each to carry the other's states.
/// </remarks>
public enum GeneratedImageOperationStatus
{
    /// <summary>Not declared. Never valid on a stored row — the check constraint refuses it.</summary>
    Unspecified = 0,

    /// <summary>Recorded and waiting for a worker to claim it.</summary>
    Requested = 1,

    /// <summary>Claimed, and the provider has been or is being called.</summary>
    Running = 2,

    /// <summary>Every variant asked for arrived and is staged.</summary>
    Succeeded = 3,

    /// <summary>
    /// Some variants arrived and some did not.
    /// </summary>
    /// <remarks>
    /// Its own state rather than a success with fewer rows: a creator who asked for four images and got two
    /// should be told, and a reader counting rows cannot tell a partial result from a request that only ever
    /// asked for two.
    /// </remarks>
    PartiallySucceeded = 4,

    /// <summary>No variant arrived. The failure category and summary say what is known.</summary>
    Failed = 5,

    /// <summary>The creator stopped it, or it was abandoned before anything was staged.</summary>
    Cancelled = 6,
}

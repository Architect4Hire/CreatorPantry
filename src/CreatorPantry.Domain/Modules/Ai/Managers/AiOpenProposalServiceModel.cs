namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// One AI request about a recipe that still needs the creator: being written, or written and holding a change they
/// have not answered (13A.15). A pointer to the request and what is true of it, never the proposal's content — that
/// is read from the request itself.
/// </summary>
/// <remarks>
/// Advice — substitutions and reviews — is never listed: it holds nothing to decide.
/// </remarks>
public sealed record AiOpenProposalServiceModel
{
    /// <summary>The request's id: what <c>GET .../ai-proposals/{id}</c> and its disposition are addressed by.</summary>
    public required Guid RequestId { get; init; }

    public required AiTaskType TaskType { get; init; }

    /// <summary><c>Requested</c> or <c>Running</c> while it is being written; <c>Proposed</c> once there is something to decide.</summary>
    public required AiOperationStatus Status { get; init; }

    public required DateTimeOffset RequestedAt { get; init; }

    /// <summary>Changes the creator has not yet accepted or rejected. Zero while it is still being written.</summary>
    public required int PendingChangeCount { get; init; }

    /// <summary>It carries a safety caution, which the creator should read before deciding.</summary>
    public required bool HasSafetyCaution { get; init; }

    /// <summary>
    /// It was asked for against a version the recipe has since moved past, so accepting it will be refused: the
    /// remedy is asking again against the recipe as it is now.
    /// </summary>
    public required bool IsStale { get; init; }
}

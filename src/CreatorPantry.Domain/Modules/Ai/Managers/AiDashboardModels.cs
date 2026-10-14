namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>One AI proposal waiting for the creator to accept or reject it.</summary>
/// <param name="OperationId">The request it answers; its proposal is read from the request's own route.</param>
/// <param name="RecipeId">The recipe it is about, when it is about one.</param>
/// <param name="ProposedAt">When it became ready to review.</param>
public sealed record AwaitingAiProposalServiceModel(Guid OperationId, AiTaskType TaskType, Guid? RecipeId, DateTimeOffset ProposedAt);

/// <summary>The workspace's AI proposals awaiting a decision: how many, and the newest.</summary>
/// <param name="Recent">At most five, newest first.</param>
public sealed record AiProposalSummaryServiceModel(int AwaitingDecision, IReadOnlyList<AwaitingAiProposalServiceModel> Recent);

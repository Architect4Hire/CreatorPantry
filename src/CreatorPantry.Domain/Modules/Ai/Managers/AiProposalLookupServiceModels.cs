namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// What one stored proposal says about itself to a module that is about to reference it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This exists so a prompt record's lineage is the server's fact rather than the client's
/// claim.</strong> PRM-001 accepts an <c>aiProposalId</c> and a template triple from the request; the triple
/// is already recorded here, on the proposal that actually produced the draft, so the save derives it from
/// this and refuses a request that disagrees (12.3a's owed decision). A prompt row is immutable, so a wrong
/// triple written once can only be erased.
/// </para>
/// <para>
/// <strong>It carries the operation's recipe pin for the other half of the same decision</strong>: a record
/// that names both a recipe and a proposal must name a proposal whose operation was about that recipe.
/// Without this, any proposal in the workspace satisfied any pin.
/// </para>
/// <para>
/// No workspace id, no membership id, no provider or model name, no token count, no payload: a caller
/// deciding whether it may reference a proposal needs its provenance and its subject, and nothing else about
/// it is theirs to read.
/// </para>
/// </remarks>
/// <param name="TaskType">
/// Which capability produced it, so a caller can refuse a proposal from the wrong one. A prompt record
/// declares its own <c>PromptRecordSource</c>, and before this any proposal in the workspace could back any
/// source — a recipe-concepts generation could be the recorded lineage of a saved image prompt.
/// </param>
/// <param name="RecipeId">The recipe the proposal's operation was about, or null when it named none.</param>
/// <param name="RecipeVersionId">The exact version it pinned, when it pinned one.</param>
public sealed record AiProposalLineageServiceModel(
    AiTaskType TaskType,
    string PromptTemplateId,
    string PromptTemplateVersion,
    string PromptTemplateBodyChecksum,
    Guid? RecipeId,
    Guid? RecipeVersionId);

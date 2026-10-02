using CreatorPantry.Domain.Modules.Ai.Data.Entities;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// Turns a stored operation and its proposal into the status reply every AI request seam answers with.
/// </summary>
/// <remarks>
/// <para>
/// One mapper rather than one per seam. Every request route in this module — recipe-bound proposals,
/// AIREC-001's concepts, AIREC-002's first drafts — polls through the same
/// <see cref="AiProposalStatusServiceModel"/>, and a creator comparing two of those replies is entitled to
/// have the same field mean the same thing in both. Three private copies of this projection would make that a
/// convention somebody has to maintain instead of a fact.
/// </para>
/// <para>
/// Projection only: no authorization, no task-type check, no decision about whether the caller should be
/// seeing this operation at all. Those belong to the Business type that loaded it, which is the layer that
/// knows what it was asked.
/// </para>
/// </remarks>
internal static class AiOperationDescription
{
    public static AiProposalStatusServiceModel Describe(AiOperation operation, AiProposal? proposal)
    {
        ArgumentNullException.ThrowIfNull(operation);

        return new AiProposalStatusServiceModel(
            operation.Id,
            operation.Status,
            operation.TaskType,
            operation.Scope,
            operation.RecipeVersionId,
            operation.RequestedAt,
            operation.StatusChangedAt,
            operation.FailureCategory,
            proposal is null ? null : Describe(proposal));
    }

    /// <summary>
    /// The proposal a creator reviews, ordered the way it was proposed.
    /// </summary>
    /// <remarks>
    /// Sorted by <c>SortOrder</c> rather than left in whatever order the query returned them: the order is the
    /// answer for the capabilities whose group membership is positional — see
    /// <see cref="RecipeFirstDraftAiTaskHandler"/>, where which group an ingredient line belongs to is recorded
    /// as nothing but its position after that group's own row.
    /// </remarks>
    private static AiProposalDetailServiceModel Describe(AiProposal proposal) =>
        new(
            proposal.Id,
            proposal.OutputSchemaVersion,
            proposal.PromptTemplateId,
            proposal.PromptTemplateVersion,
            proposal.PromptTemplateBodyChecksum,
            proposal.ProviderName,
            proposal.ModelName,
            proposal.CreatedAt,
            [.. proposal.Changes
                .OrderBy(change => change.SortOrder)
                .Select(change => new AiProposedChangeServiceModel(
                    change.Id,
                    change.ChangeKind,
                    change.TargetKind,
                    change.TargetId,
                    change.FieldName,
                    change.BeforeValue,
                    change.AfterValue,
                    change.ProposedPosition,
                    change.Disposition))],
            [.. proposal.Warnings
                .OrderBy(warning => warning.SortOrder)
                .Select(warning => new AiProposalWarningServiceModel(
                    warning.Kind, warning.Message, warning.AiStructuredChangeId))],
            Describe(proposal.BrandContext));

    /// <remarks>
    /// Null in two cases that look alike from here and are not: the generation asked for no brand context, or this
    /// proposal was read without it loaded. Only one read publishes a detail model and it includes the rows, so
    /// the second case is a mapping defect rather than a state a caller can reach.
    /// </remarks>
    private static AiProposalBrandContextServiceModel? Describe(AiProposalBrandContext? context) =>
        context is null
            ? null
            : new AiProposalBrandContextServiceModel(
                context.ChannelKey,
                context.Audience,
                context.AudienceOrigin,
                context.BrandProfileRevision,
                context.BrandGuideId,
                context.BrandGuideVersionId,
                context.BrandGuideVersionNumber,
                context.GuideWasActiveVersion,
                context.Checksum,
                context.EstimatedTokens,
                context.GuidanceSectionCount,
                context.RuleCount,
                [.. context.Sources
                    .OrderBy(source => source.SortOrder)
                    .Select(source => new AiProposalBrandSourceServiceModel(
                        source.BrandSourceDocumentId,
                        source.DocumentVersionNumber,
                        source.BrandSourcePassageId,
                        source.Ordinal))],
                context.AssembledAt);
}

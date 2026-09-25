using CreatorPantry.Domain.Modules.Ai.Data.Entities;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>Which template, body and model produced a proposal. Recorded on it for traceability.</summary>
public sealed record AiProposalProvenance(
    string OutputSchemaVersion,
    string PromptTemplateId,
    string PromptTemplateVersion,
    string PromptTemplateBodyChecksum,
    string ProviderName,
    string ModelName,
    string? ModelDeployment);

/// <summary>An assembled proposal ready to store, or the reason it cannot be.</summary>
public sealed record AiProposalAssembly(AiProposal? Proposal, AiOutputFailure? Failure)
{
    public bool Succeeded => Failure is null;
}

/// <summary>
/// Turns a validated answer and its server-calculated diff into the rows a proposal is stored as.
/// </summary>
/// <remarks>
/// <para>
/// Pure, and the last stage before persistence. It also holds the staleness rule, because this is the point
/// where the pinned version and the recipe's current version are both known.
/// </para>
/// <para>
/// <strong>Loading the snapshot is not done here.</strong> The caller supplies it, because obtaining the exact
/// authorized recipe version is the worker's job and doing it here would mean this module reaching into the
/// recipe module for a read that does not exist yet.
/// </para>
/// </remarks>
public static class AiProposalAssembler
{
    /// <param name="pinnedVersionId">The version the proposal was computed against.</param>
    /// <param name="currentVersionId">
    /// The recipe's current version now. When these differ the recipe has moved on and the proposal is stale.
    /// </param>
    public static AiProposalAssembly Assemble(
        Guid workspaceId,
        Guid operationId,
        Guid? pinnedVersionId,
        Guid? currentVersionId,
        AiOutputDocument output,
        IReadOnlyList<AiResolvedChange> diff,
        AiProposalProvenance provenance,
        DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(diff);
        ArgumentNullException.ThrowIfNull(provenance);

        // The restriction, in one place: if the source has moved, fail — never silently rebase. A proposal's
        // before values and its target ids were all read from the pinned version, so applying it to a newer one
        // would apply a diff to content it was never computed from. Re-requesting against the new version is
        // the creator's remedy, and it is theirs to choose.
        if (pinnedVersionId != currentVersionId)
        {
            return new AiProposalAssembly(null, new AiOutputFailure(
                AiFailureCategory.DomainInvalid,
                AiOutputReason.SourceChanged,
                "The recipe changed after this proposal was computed, so it was not applied to a version it "
                    + "was never based on.",
                IsCorrectableByReprompt: false));
        }

        var proposal = new AiProposal
        {
            WorkspaceId = workspaceId,
            AiOperationId = operationId,
            SourceRecipeVersionId = pinnedVersionId,
            OutputSchemaVersion = provenance.OutputSchemaVersion,
            PromptTemplateId = provenance.PromptTemplateId,
            PromptTemplateVersion = provenance.PromptTemplateVersion,
            PromptTemplateBodyChecksum = provenance.PromptTemplateBodyChecksum,
            ProviderName = provenance.ProviderName,
            ModelName = provenance.ModelName,
            ModelDeployment = provenance.ModelDeployment,
            CreatedAt = createdAt,
        };

        var changes = diff
            .Select(resolved => new AiStructuredChange
            {
                WorkspaceId = workspaceId,
                ChangeKind = resolved.ChangeKind,
                TargetKind = resolved.TargetKind,
                TargetId = resolved.TargetId,
                FieldName = resolved.FieldName,

                // From the diff, which read it from the snapshot. Never from the answer.
                BeforeValue = resolved.BeforeValue,
                AfterValue = resolved.AfterValue,
                ProposedPosition = resolved.ProposedPosition,
                SortOrder = resolved.SortOrder,
                Disposition = AiChangeDisposition.Pending,
            })
            .ToList();

        foreach (var change in changes)
        {
            proposal.Changes.Add(change);
        }

        // A warning names its change by index into the answer; the stored row names it by id. The diff
        // preserves the answer's order as SortOrder, so the index resolves to exactly one change.
        for (var index = 0; index < output.Warnings.Count; index++)
        {
            var warning = output.Warnings[index];

            proposal.Warnings.Add(new AiWarning
            {
                WorkspaceId = workspaceId,
                Kind = warning.Kind,
                Message = warning.Message,
                AiStructuredChangeId = warning.ChangeIndex is { } changeIndex
                    ? changes.FirstOrDefault(change => change.SortOrder == changeIndex)?.Id
                    : null,
                SortOrder = index,
            });
        }

        return new AiProposalAssembly(proposal, null);
    }
}

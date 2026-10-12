using System.Globalization;
using System.Text.Json;
using CreatorPantry.Domain.Modules.Ai.Data;
using CreatorPantry.Domain.Modules.Ai.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Facade;
using CreatorPantry.Domain.Modules.Content.Managers;
using Microsoft.Extensions.Logging;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// Turns a stored <see cref="AiTaskType.LongFormOutline"/> proposal into the next revision of its long-form
/// draft (LF.2.2).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Run by the worker once the proposal has committed</strong>, and again by the maintenance sweep for a
/// while afterwards, so it is idempotent: the content module stores one revision per proposal and answers a
/// second landing with the draft as it stands.
/// </para>
/// <para>
/// <strong>Everything it stores was on the proposal.</strong> The sections are the plan rows; the recipe pins
/// are the pin rows the handler wrote from the package it read. Nothing is read again here, so the revision is
/// pinned to what the plan was written against and not to whatever the piece names a moment later.
/// </para>
/// <para>
/// <strong>The draft is named, and made if it is not there.</strong> A request that starts a draft queues the
/// operation first, so a fast worker can reach this before the draft row exists.
/// </para>
/// </remarks>
internal sealed class LongFormOutlineProposalLanding(
    IAiOperationDataLayer operations,
    ILongFormDraftFacade drafts,
    ILogger<LongFormOutlineProposalLanding> logger) : IAiProposalLandingHandler
{
    public async Task LandAsync(Guid operationId, CancellationToken cancellationToken)
    {
        var proposal = await operations.FindProposalForLandingAsync(operationId, cancellationToken);

        // No proposal and no creative-context provenance are both "nothing to land": a failed run, or a
        // proposal from some other task.
        if (proposal?.CreativeContext is not { } context)
        {
            return;
        }

        var operation = await operations.GetWithProposalAsync(operationId, cancellationToken);
        var inputs = operation?.Operation.TaskInputsJson is { } json
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(json)
            : null;

        if (LongFormOutlineInputs.ReadKind(inputs) is not { } kind)
        {
            return;
        }

        var sections = ReadSections(proposal);

        if (sections.Count == 0)
        {
            return;
        }

        var recorded = await drafts.RecordGeneratedPlanAsync(
            new LongFormGeneratedPlanInput(
                LongFormOutlineInputs.ReadDraftId(inputs, operationId),
                context.CreativeContextId,
                kind,
                proposal.Id,
                proposal.PromptTemplateId,
                proposal.PromptTemplateVersion,
                proposal.PromptTemplateBodyChecksum,
                proposal.BrandContext?.BrandGuideVersionId,
                context.ContextVersion,
                sections,
                ReadPins(proposal)),
            cancellationToken);

        if (!recorded.Succeeded)
        {
            // The code is the server's own and the ids are identifiers; no word of the plan is logged (ai.md).
            logger.LogError(
                "The plan of AI operation {OperationId} could not be stored: {Code}.", operationId, recorded.Error!.Code);
        }
    }

    /// <summary>One section per <see cref="AiChangeTargetKind.LongFormSectionPlan"/> target, in plan order.</summary>
    private static List<LongFormGeneratedSectionInput> ReadSections(AiProposal proposal)
    {
        var sections = new List<LongFormGeneratedSectionInput>();

        foreach (var target in Targets(proposal, AiChangeTargetKind.LongFormSectionPlan)
            .Where(target => target.Heading is not null)
            .OrderBy(target => target.Heading!.ProposedPosition ?? int.MaxValue)
            .ThenBy(target => target.Heading!.SortOrder))
        {
            // A row this cannot read is a proposal this version of the code did not write. The whole plan is
            // left unlanded rather than landed with a section missing: a plan with a hole in it reads as whole.
            if (target.Heading!.AfterValue is not { } heading
                || target.Fields.GetValueOrDefault(LongFormPlanFields.Intent) is not { } intent
                || !int.TryParse(
                    target.Fields.GetValueOrDefault(LongFormPlanFields.Role),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var role)
                || !Enum.IsDefined((LongFormSectionRole)role))
            {
                return [];
            }

            Guid? source = null;

            if (target.Fields.GetValueOrDefault(LongFormPlanFields.SourceReferenceId) is { } value)
            {
                if (!Guid.TryParse(value, out var parsed))
                {
                    return [];
                }

                source = parsed;
            }

            sections.Add(new LongFormGeneratedSectionInput((LongFormSectionRole)role, heading, intent, source));
        }

        return sections;
    }

    /// <summary>One pin per <see cref="AiChangeTargetKind.LongFormRecipePin"/> target, in the order the piece names them.</summary>
    private static List<LongFormRecipePinInput> ReadPins(AiProposal proposal) =>
    [
        .. Targets(proposal, AiChangeTargetKind.LongFormRecipePin)
            .Where(target =>
                Guid.TryParse(target.Fields.GetValueOrDefault(LongFormPinFields.RecipeId), out _)
                && Guid.TryParse(target.Fields.GetValueOrDefault(LongFormPinFields.RecipeVersionId), out _))
            .OrderBy(target => target.FirstRow)
            .Select(target => new LongFormRecipePinInput(
                target.Id,
                Guid.Parse(target.Fields[LongFormPinFields.RecipeId]!),
                Guid.Parse(target.Fields[LongFormPinFields.RecipeVersionId]!))),
    ];

    private static IEnumerable<Target> Targets(AiProposal proposal, AiChangeTargetKind kind) =>
        proposal.Changes
            .Where(change => change.TargetKind == kind && change.TargetId is not null)
            .GroupBy(change => change.TargetId!.Value)
            .Select(rows => new Target(
                rows.Key,
                rows.FirstOrDefault(row => row.ChangeKind is AiChangeKind.Add),
                rows.Where(row => row.ChangeKind is AiChangeKind.Set && row.FieldName is not null)
                    .ToDictionary(row => row.FieldName!, row => row.AfterValue, StringComparer.Ordinal),
                rows.Min(row => row.SortOrder)));

    private sealed record Target(
        Guid Id, AiStructuredChange? Heading, IReadOnlyDictionary<string, string?> Fields, int FirstRow);
}

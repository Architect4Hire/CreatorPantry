using System.Globalization;
using System.Text.Json;
using CreatorPantry.Domain.Modules.Ai.Data;
using CreatorPantry.Domain.Modules.Content.Facade;
using CreatorPantry.Domain.Modules.Content.Managers;
using Microsoft.Extensions.Logging;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// Puts a stored section body into its draft as the draft's next revision (LF.3.3): the step
/// <see cref="LongFormSectionAiTaskHandler"/> stops short of.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Which draft and which section come from the operation, never from the proposal.</strong> The
/// operation row is read under the workspace filter of the context the worker resolved, and its inputs name the
/// draft. The proposal's rows say which section the body is for, and are believed only when they agree with
/// the operation: a body is never landed on a section the request did not name.
/// </para>
/// <para>
/// <strong>The creator's text outranks the answer.</strong> If the section was edited or settled after the
/// request was made, the content module declines the body. That is an outcome and not a fault: the proposal
/// stays stored, the section stays as the creator left it, and the section list reports the request as
/// superseded.
/// </para>
/// <para>
/// <strong>Idempotent, because it is run more than once.</strong> The worker lands a proposal as soon as it
/// commits and the maintenance sweep lands it again for a while afterwards; the content module finds the
/// revision the first landing wrote and writes no second.
/// </para>
/// </remarks>
internal sealed class LongFormSectionProposalLanding(
    IAiOperationDataLayer operations,
    ILongFormSectionFacade sections,
    ILogger<LongFormSectionProposalLanding> logger) : IAiProposalLandingHandler
{
    public async Task LandAsync(Guid operationId, CancellationToken cancellationToken)
    {
        var proposal = await operations.FindProposalForLandingAsync(operationId, cancellationToken);

        if (proposal is null)
        {
            return;
        }

        var operation = await operations.GetWithProposalAsync(operationId, cancellationToken);
        var inputs = operation?.Operation.TaskInputsJson is { } json
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(json)
            : null;

        if (operation?.Operation.TaskType is not AiTaskType.LongFormSection
            || LongFormSectionInputs.ReadDraftId(inputs) is not { } draftId
            || LongFormSectionInputs.ReadSectionId(inputs) is not { } sectionId)
        {
            return;
        }

        var rows = proposal.Changes
            .Where(change => change.TargetKind is AiChangeTargetKind.LongFormSectionBody)
            .ToList();

        // Rows this cannot read are a proposal this version of the code did not write, and rows for another
        // section are not this request's answer. Left unlanded either way: nothing is guessed into a draft.
        if (rows.Count == 0
            || rows.Any(row => row.TargetId != sectionId || row.FieldName is null || row.AfterValue is null))
        {
            return;
        }

        var fields = rows.ToDictionary(row => row.FieldName!, row => row.AfterValue!, StringComparer.Ordinal);
        var body = LongFormSectionFields.Join(fields);

        if (string.IsNullOrWhiteSpace(body)
            || !int.TryParse(
                fields.GetValueOrDefault(LongFormSectionFields.BasedOnRevision),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var basedOn))
        {
            return;
        }

        var recorded = await sections.RecordGeneratedSectionAsync(
            new LongFormGeneratedSectionBodyInput(
                draftId,
                sectionId,
                basedOn,
                body,
                proposal.Id,
                proposal.PromptTemplateId,
                proposal.PromptTemplateVersion,
                proposal.PromptTemplateBodyChecksum,
                fields.GetValueOrDefault(LongFormSectionFields.ReusedEditorialParts)?
                    .Split(LongFormSectionFields.ReusedPartsSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)),
            cancellationToken);

        if (recorded.Succeeded || recorded.Error!.Code is ContentErrorCodes.LongFormSectionSuperseded)
        {
            return;
        }

        // The code is the server's own and the ids are identifiers; no word of the section is logged (ai.md).
        logger.LogError(
            "The section body of AI operation {OperationId} could not be stored: {Code}.", operationId, recorded.Error.Code);
    }
}

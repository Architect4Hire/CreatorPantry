using System.Globalization;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Data;
using CreatorPantry.Domain.Modules.Ai.Data.Entities;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.Content.Facade;
using CreatorPantry.Domain.Modules.Content.Managers;

namespace CreatorPantry.Domain.Modules.Ai.Business;

internal interface IAiLongFormSectionRequestBusiness
{
    Task<IdempotentOutcome<LongFormSectionRequestedServiceModel>> RequestAsync(
        Guid draftId, Guid sectionId, RequestLongFormSectionViewModel model, string idempotencyKey, CancellationToken cancellationToken);

    Task<IdempotentOutcome<LongFormSectionBatchServiceModel>> RequestRestAsync(
        Guid draftId, RequestLongFormSectionBatchViewModel model, string idempotencyKey, CancellationToken cancellationToken);

    Task<OperationResult<LongFormSectionsStatusServiceModel>> GetSectionsAsync(Guid draftId, CancellationToken cancellationToken);

    Task<OperationResult<LongFormDocumentReviewServiceModel>> GetDocumentAsync(Guid draftId, CancellationToken cancellationToken);
}

/// <summary>
/// Queues the writing of a long-form draft's sections, one operation to a section, and says where each
/// section stands (LF.3.3).
/// </summary>
/// <remarks>
/// <para>
/// <strong>A replay is answered before anything is checked.</strong> A retried request must get the answer the
/// first one got, and by then the first one may have done its work: the section is written, so it is no longer
/// "unchanged since the revision named". Checked first, that retry would be refused for the very thing it
/// caused. So the key is looked up first, and the checks are for requests that are new.
/// </para>
/// <para>
/// <strong>Nothing is spent to refuse.</strong> The allowance is peeked before an operation is written. For
/// "write the rest" the peek is weighed against every section it would queue, and when it cannot cover them
/// all, none is queued and the reply says how many it could have covered.
/// </para>
/// <para>
/// <strong>The peek is a snapshot; the hold is per section.</strong> Each operation takes its own hold when the
/// worker starts it, as every AI operation does. If the allowance runs out part-way through a fan-out, the
/// sections not yet started fail one by one with a quota failure that the section list reports. Nothing already
/// written is touched.
/// </para>
/// <para>
/// <strong>The first new request closes the plan.</strong> The draft moves to <c>Writing</c> after the
/// allowance check and before the operation is queued, so a refused request leaves an approved plan editable.
/// </para>
/// </remarks>
internal sealed class AiLongFormSectionRequestBusiness(
    IAiOperationDataLayer operations,
    IAiRequestQuotaGate quota,
    ILongFormSectionFacade sections,
    ILongFormDocumentFacade documents,
    IWorkspaceContext workspace,
    IAiTaskGate tasks,
    IClock clock) : IAiLongFormSectionRequestBusiness
{
    public async Task<IdempotentOutcome<LongFormSectionRequestedServiceModel>> RequestAsync(
        Guid draftId, Guid sectionId, RequestLongFormSectionViewModel model, string idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (!await tasks.IsEnabledAsync(AiTaskCatalog.LongFormSection, cancellationToken))
        {
            return Refuse<LongFormSectionRequestedServiceModel>(NotEnabled());
        }

        // The content module's own 404, for an unknown draft and another workspace's alike — before the key is
        // looked at, so a key cannot be used to ask whether a draft exists.
        var found = await sections.GetSectionsAsync(draftId, cancellationToken);

        if (!found.Succeeded)
        {
            return Refuse<LongFormSectionRequestedServiceModel>(found.Error!);
        }

        var instruction = string.IsNullOrWhiteSpace(model.Instruction) ? null : model.Instruction;
        var operation = Operation(draftId, sectionId, model.RevisionNumber, instruction, idempotencyKey, clock.UtcNow);

        if (await operations.FindReplayAsync(operation, cancellationToken) is { } replay)
        {
            return replay.Operation is { } existing
                ? Requested(found.Value!, sectionId, existing, replayed: true)
                : Refuse<LongFormSectionRequestedServiceModel>(KeyReused());
        }

        // Asked of the content module exactly as the worker will ask it, so a request that could not be run is
        // refused here rather than queued, paid for and failed: an unapproved or finished draft, a section that
        // is not in the plan, and a section whose text has changed since the revision named.
        var writable = await sections.GetWritingContextAsync(draftId, model.RevisionNumber, sectionId, cancellationToken);

        if (!writable.Succeeded)
        {
            return Refuse<LongFormSectionRequestedServiceModel>(writable.Error!);
        }

        if (await quota.RefuseIfUnaffordableAsync(AiTaskType.LongFormSection, cancellationToken, idempotencyKey) is { } spent)
        {
            return Refuse<LongFormSectionRequestedServiceModel>(spent.Result.Error!);
        }

        var begun = await sections.BeginWritingAsync(draftId, cancellationToken);

        if (!begun.Succeeded)
        {
            return Refuse<LongFormSectionRequestedServiceModel>(begun.Error!);
        }

        var queued = await operations.RequestAsync(operation, cancellationToken);

        return queued.Operation is { } created
            ? Requested(begun.Value!, sectionId, created, queued.Outcome is AiOperationRequestOutcome.Replayed)
            : Refuse<LongFormSectionRequestedServiceModel>(KeyReused());
    }

    public async Task<IdempotentOutcome<LongFormSectionBatchServiceModel>> RequestRestAsync(
        Guid draftId, RequestLongFormSectionBatchViewModel model, string idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (!await tasks.IsEnabledAsync(AiTaskCatalog.LongFormSection, cancellationToken))
        {
            return Refuse<LongFormSectionBatchServiceModel>(NotEnabled());
        }

        // The content module's own 404 first, for an unknown draft and another workspace's alike.
        var known = await sections.GetSectionsAsync(draftId, cancellationToken);

        if (!known.Succeeded)
        {
            return Refuse<LongFormSectionBatchServiceModel>(known.Error!);
        }

        var now = clock.UtcNow;

        // A replay is answered before anything is checked, as it is for one section. The first attempt's
        // requests are found by the keys it stored them under, for every section of the plan and not only the
        // ones still unwritten — by now that attempt may have written them all.
        var earlier = new Dictionary<Guid, AiOperation>();

        foreach (var section in known.Value!.Sections)
        {
            var probe = Operation(
                draftId, section.Id, model.RevisionNumber, instruction: null,
                AiLongFormSectionRequestPolicy.BatchKey(idempotencyKey, section.Id), now);

            if (await operations.FindReplayAsync(probe, cancellationToken) is not { } replay)
            {
                continue;
            }

            if (replay.Operation is not { } existing)
            {
                return Refuse<LongFormSectionBatchServiceModel>(KeyReused());
            }

            earlier[section.Id] = existing;
        }

        if (earlier.Count > 0)
        {
            return new IdempotentOutcome<LongFormSectionBatchServiceModel>(
                OperationResult<LongFormSectionBatchServiceModel>.Success(new(
                    known.Value,
                    [
                        .. known.Value.Sections
                            .Where(section => earlier.ContainsKey(section.Id))
                            .Select(section => new LongFormSectionWritingRequestServiceModel(
                                section.Id, AiOperationDescription.Describe(earlier[section.Id], null))),
                    ])),
                Replayed: true);
        }

        var found = await sections.GetWritableSectionsAsync(draftId, cancellationToken);

        if (!found.Succeeded)
        {
            return Refuse<LongFormSectionBatchServiceModel>(found.Error!);
        }

        var draft = found.Value!;

        // A revision the draft has not reached is not one the request could have been composed against.
        if (draft.RevisionNumber is not { } latest || model.RevisionNumber > latest)
        {
            return Refuse<LongFormSectionBatchServiceModel>(new OperationError(
                ContentErrorCodes.LongFormRevisionStale,
                "This draft has changed since the request was made. Reload it and ask again.",
                new Dictionary<string, string[]>()));
        }

        var busy = (await ListRequestsAsync(draftId, cancellationToken))
            .Where(request => request.Operation.Status is AiOperationStatus.Requested or AiOperationStatus.Running)
            .Select(request => request.SectionId)
            .ToHashSet();

        var replayed = new Dictionary<Guid, AiOperation>();
        var fresh = new List<(Guid SectionId, AiOperation Operation)>();

        // In plan order, which is the order they are queued in and the order the reply lists them in. A
        // section already being written by an earlier request is left to it: a second run would be a second
        // charge for one section, and whichever finished last would be refused as superseded.
        foreach (var section in draft.Sections.Where(section =>
                     !section.IsWritten && !section.Settled && !busy.Contains(section.Id)))
        {
            fresh.Add((section.Id, Operation(
                draftId,
                section.Id,
                model.RevisionNumber,
                instruction: null,
                AiLongFormSectionRequestPolicy.BatchKey(idempotencyKey, section.Id),
                now)));
        }

        if (fresh.Count > 0)
        {
            if (await quota.RefuseIfRunsUnaffordableAsync(
                    AiTaskType.LongFormSection,
                    fresh.Count,
                    AiLongFormSectionRequestErrors.SectionsRequestedExtension,
                    AiLongFormSectionRequestErrors.SectionsCoverableExtension,
                    cancellationToken) is { } uncovered)
            {
                return Refuse<LongFormSectionBatchServiceModel>(uncovered);
            }

            var begun = await sections.BeginWritingAsync(draftId, cancellationToken);

            if (!begun.Succeeded)
            {
                return Refuse<LongFormSectionBatchServiceModel>(begun.Error!);
            }

            draft = begun.Value!;

            // A moment apart and ending now, so the worker's earliest-first claim takes them in plan order and
            // none of them waits to become available. Nothing depends on the order: each run reads whatever is
            // written when it starts.
            for (var index = 0; index < fresh.Count; index++)
            {
                fresh[index].Operation.AvailableAt =
                    now - (AiLongFormSectionRequestPolicy.BatchSpacing * (fresh.Count - 1 - index));
            }

            foreach (var (sectionId, operation) in fresh)
            {
                var queued = await operations.RequestAsync(operation, cancellationToken);

                if (queued.Operation is not { } created)
                {
                    return Refuse<LongFormSectionBatchServiceModel>(KeyReused());
                }

                replayed[sectionId] = created;
            }
        }

        return new IdempotentOutcome<LongFormSectionBatchServiceModel>(
            OperationResult<LongFormSectionBatchServiceModel>.Success(new(
                draft,
                [
                    .. draft.Sections
                        .Where(section => replayed.ContainsKey(section.Id))
                        .Select(section => new LongFormSectionWritingRequestServiceModel(
                            section.Id, AiOperationDescription.Describe(replayed[section.Id], null))),
                ])),
            Replayed: false);
    }

    public async Task<OperationResult<LongFormSectionsStatusServiceModel>> GetSectionsAsync(
        Guid draftId, CancellationToken cancellationToken)
    {
        // A read, and only a read. A body becomes the section's text in the worker, straight after its proposal
        // commits — asking where things stand must not be what writes a creator's content.
        var found = await sections.GetSectionsAsync(draftId, cancellationToken);

        if (!found.Succeeded)
        {
            return OperationResult<LongFormSectionsStatusServiceModel>.Failure(found.Error!);
        }

        var draft = found.Value!;

        return OperationResult<LongFormSectionsStatusServiceModel>.Success(new(
            draft,
            await SummariseAsync(draftId, [.. draft.Sections.Select(section => (section.Id, section.AiProposalId))], cancellationToken)));
    }

    public async Task<OperationResult<LongFormDocumentReviewServiceModel>> GetDocumentAsync(
        Guid draftId, CancellationToken cancellationToken)
    {
        // A read, and only a read: nothing is assembled into storage, and nothing is spent.
        var found = await documents.GetDocumentAsync(draftId, cancellationToken);

        if (!found.Succeeded)
        {
            return OperationResult<LongFormDocumentReviewServiceModel>.Failure(found.Error!);
        }

        var document = found.Value!;

        // The proposals whose text is the document's text now, each with the section it wrote.
        var written = document.Sections
            .Where(section => section.IsWritten && section.AiProposalId is not null)
            .ToDictionary(section => section.AiProposalId!.Value, section => section.Id);

        var stored = await operations.ListWarningsAsync(written.Keys, cancellationToken);
        var order = document.Sections.Select((section, index) => (section.Id, index)).ToDictionary(item => item.Id, item => item.index);

        return OperationResult<LongFormDocumentReviewServiceModel>.Success(new(
            document,
            [
                .. stored
                    .Where(warning => written.ContainsKey(warning.AiProposalId))

                    // Claim findings are the section's own now (LF.3.2), recorded when its text was written.
                    // A proposal stored before that carries its own copy of them, which is not shown twice.
                    .Where(warning => !IsClaimFinding(warning.Message))
                    .OrderBy(warning => order[written[warning.AiProposalId]])
                    .ThenBy(warning => warning.SortOrder)
                    .Select(warning => Describe(written[warning.AiProposalId], warning)),
            ],
            await SummariseAsync(draftId, [.. document.Sections.Select(section => (section.Id, section.AiProposalId))], cancellationToken)));
    }

    /// <summary>
    /// A stored warning as a creator is shown it: the server's code and the model's label taken off the front
    /// of the sentence and reported as what they are.
    /// </summary>
    internal static LongFormSectionWarningServiceModel Describe(Guid sectionId, AiWarning warning)
    {
        var message = warning.Message;

        if (message.StartsWith(AiPolicy.ModelWarningLabel, StringComparison.Ordinal))
        {
            return new(sectionId, warning.Kind, Code: null, message[AiPolicy.ModelWarningLabel.Length..], FromModel: true);
        }

        var close = message.StartsWith('[') ? message.IndexOf("] ", StringComparison.Ordinal) : -1;

        return close > 1
            ? new(sectionId, warning.Kind, message[1..close], message[(close + 2)..], FromModel: false)
            : new(sectionId, warning.Kind, Code: null, message, FromModel: false);
    }

    private static bool IsClaimFinding(string message) =>
        message.StartsWith("[editorial.", StringComparison.Ordinal)
        || message.StartsWith("[seo.", StringComparison.Ordinal)
        || message.StartsWith($"[{LongFormClaimScanner.UnsupportedGuarantee}]", StringComparison.Ordinal);

    /// <summary>The latest writing request for each of these sections that has one, in the order given.</summary>
    private async Task<List<LongFormSectionRequestSummaryServiceModel>> SummariseAsync(
        Guid draftId, IReadOnlyList<(Guid Id, Guid? AiProposalId)> inPlan, CancellationToken cancellationToken)
    {
        var known = inPlan.Select(section => section.Id).ToHashSet();

        // The list is newest first, so the first of each group is the latest.
        var latest = (await ListRequestsAsync(draftId, cancellationToken))
            .Where(request => known.Contains(request.SectionId))
            .GroupBy(request => request.SectionId)
            .Select(group => group.First())
            .ToDictionary(request => request.SectionId);

        return
        [
            .. inPlan
                .Where(section => latest.ContainsKey(section.Id))
                .Select(section =>
                {
                    var request = latest[section.Id];

                    return new LongFormSectionRequestSummaryServiceModel(
                        section.Id,
                        request.Operation.Id,
                        request.Operation.Status,
                        request.Operation.FailureCategory,
                        Outcome(request, section.AiProposalId),
                        request.Operation.RequestedAt);
                }),
        ];
    }

    private static LongFormSectionRequestOutcome Outcome(
        AiLongFormSectionRequestRow request, Guid? sectionProposalId) => request.Operation.Status switch
    {
        AiOperationStatus.Requested or AiOperationStatus.Running => LongFormSectionRequestOutcome.Waiting,
        AiOperationStatus.Failed or AiOperationStatus.Rejected or AiOperationStatus.Expired => LongFormSectionRequestOutcome.Failed,

        // Written means its text is the section's text now, which is a fact about the section and not about
        // the request: a proposal the creator has since typed over is no longer what the section says.
        _ => request.ProposalId is { } proposal && sectionProposalId == proposal
            ? LongFormSectionRequestOutcome.Written
            : LongFormSectionRequestOutcome.Superseded,
    };

    /// <summary>
    /// This draft's section-writing requests, newest first.
    /// </summary>
    /// <remarks>
    /// Found by the draft's id in the stored inputs, under the workspace filter, and then read properly: a row
    /// is this draft's only if its inputs, parsed, name this draft. The text match narrows; it decides nothing.
    /// </remarks>
    private async Task<List<AiLongFormSectionRequestRow>> ListRequestsAsync(Guid draftId, CancellationToken cancellationToken)
    {
        var rows = await operations.ListByTaskInputAsync(
            AiTaskType.LongFormSection,
            draftId.ToString("D"),
            AiLongFormSectionRequestPolicy.StatusLookback,
            cancellationToken);

        var requests = new List<AiLongFormSectionRequestRow>(rows.Count);

        foreach (var (operation, proposalId) in rows)
        {
            var inputs = operation.TaskInputsJson is { } json
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                : null;

            if (LongFormSectionInputs.ReadDraftId(inputs) == draftId && LongFormSectionInputs.ReadSectionId(inputs) is { } sectionId)
            {
                requests.Add(new AiLongFormSectionRequestRow(sectionId, operation, proposalId));
            }
        }

        return requests;
    }

    private AiOperation Operation(
        Guid draftId, Guid sectionId, int revisionNumber, string? instruction, string idempotencyKey, DateTimeOffset availableAt)
    {
        var now = clock.UtcNow;

        var inputs = new Dictionary<string, string>(4, StringComparer.Ordinal)
        {
            [LongFormSectionInputs.LongFormDraftId] = draftId.ToString("D"),
            [LongFormSectionInputs.RevisionNumber] = revisionNumber.ToString(CultureInfo.InvariantCulture),
            [LongFormSectionInputs.SectionId] = sectionId.ToString("D"),
        };

        if (instruction is not null)
        {
            inputs[LongFormSectionInputs.Instruction] = instruction;
        }

        return new AiOperation
        {
            WorkspaceId = workspace.WorkspaceId,
            TaskType = AiTaskType.LongFormSection,

            // Not taken from the request, and NotApplicable: the operation names no recipe, and the scope is
            // what makes "no stored row here has a path to a recipe edit" a property of the row.
            Scope = AiOperationScope.NotApplicable,
            Status = AiOperationStatus.Requested,
            RecipeId = null,
            RecipeVersionId = null,
            IdempotencyKey = idempotencyKey,
            TaskInputsJson = JsonSerializer.Serialize(inputs),
            RequestedByMembershipId = workspace.MembershipId,
            RequestedAt = now,
            StatusChangedAt = now,
            AvailableAt = availableAt,
        };
    }

    private static IdempotentOutcome<LongFormSectionRequestedServiceModel> Requested(
        LongFormSectionsServiceModel draft, Guid sectionId, AiOperation operation, bool replayed) =>
        new(
            OperationResult<LongFormSectionRequestedServiceModel>.Success(new(
                draft,
                new LongFormSectionWritingRequestServiceModel(sectionId, AiOperationDescription.Describe(operation, null)))),
            Replayed: replayed);

    private static OperationError NotEnabled() => new(
        AiLongFormSectionRequestErrors.TaskNotEnabled,
        "Writing the sections of a long-form piece is not switched on for this workspace.",
        new Dictionary<string, string[]>());

    private static OperationError KeyReused() => new(
        IdempotencyPolicy.KeyReusedCode,
        "That idempotency key was already used for a different request.",
        new Dictionary<string, string[]>());

    private static IdempotentOutcome<T> Refuse<T>(OperationError error) =>
        new(OperationResult<T>.Failure(error), Replayed: false);

    private sealed record AiLongFormSectionRequestRow(Guid SectionId, AiOperation Operation, Guid? ProposalId);
}

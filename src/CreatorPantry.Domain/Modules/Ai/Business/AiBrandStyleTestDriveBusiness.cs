using System.Text.Json;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Data;
using CreatorPantry.Domain.Modules.Ai.Data.Entities;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.Brand.Facade;
using CreatorPantry.Domain.Modules.Brand.Managers;

namespace CreatorPantry.Domain.Modules.Ai.Business;

internal interface IAiBrandStyleTestDriveBusiness
{
    Task<IdempotentOutcome<BrandStyleTestDriveServiceModel>> RequestAsync(
        RequestBrandStyleTestDriveViewModel model, string idempotencyKey, CancellationToken cancellationToken);

    Task<OperationResult<BrandStyleTestDriveServiceModel>> GetAsync(
        Guid requestId, CancellationToken cancellationToken);
}

/// <summary>
/// Queues a style test drive for one guide version (11A.24), and reads back the comparison it produced.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The guide is resolved through the assembler, not through a second read of its own.</strong> The
/// assembler is what the worker will use, so asking it here answers both questions the edge needs — does this
/// version resolve at all, and does it hold anything a test drive could demonstrate — in the terms the
/// generation will actually see. It makes no provider call, so the refusals below cost nothing.
/// </para>
/// <para>
/// <strong>An empty guide version is refused here as well as in the handler.</strong> Not duplication for its
/// own sake: refusing at the edge tells the creator synchronously, while the handler's check is what holds if a
/// guide is emptied between the request and the run. The quota gate is paired the same way, for the same reason.
/// </para>
/// <para>
/// <strong>The read side re-derives which rules applied and says when that has moved.</strong> Which sections
/// were selected is deterministic from the stored guide version, so it is recomputed rather than stored; what
/// cannot be recomputed is whether the guide still reads the way it did, so the package's checksum is compared
/// with the one recorded against the proposal and the difference is published rather than hidden. The samples
/// themselves are never re-derived — they are what the model wrote.
/// </para>
/// </remarks>
internal sealed class AiBrandStyleTestDriveBusiness(
    IAiOperationDataLayer operations,
    IAiRequestQuotaGate quota,
    IBrandContextAssembler brandContext,
    IBrandSourceDocumentFacade documents,
    IWorkspaceContext workspace,
    AiTaskOptions tasks,
    IClock clock) : IAiBrandStyleTestDriveBusiness
{
    /// <summary>How many of the guide's do/don't rules the comparison names, matching the writing preview's.</summary>
    private const int MaxRules = 5;

    public async Task<IdempotentOutcome<BrandStyleTestDriveServiceModel>> RequestAsync(
        RequestBrandStyleTestDriveViewModel model, string idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (!AiTaskCatalog.IsEnabled(tasks, AiTaskCatalog.BrandStyleTestDrive))
        {
            return Refuse(
                AiBrandStyleTestDriveRequestErrors.TaskNotEnabled,
                "Trying your style out is not enabled for this workspace's deployment.");
        }

        if (await quota.RefuseIfUnaffordableAsync(AiTaskType.BrandStyleTestDrive, cancellationToken) is { } spent)
        {
            return new IdempotentOutcome<BrandStyleTestDriveServiceModel>(
                OperationResult<BrandStyleTestDriveServiceModel>.Failure(spent.Result.Error!), Replayed: false);
        }

        var selection = new BrandContextRequestSelection(
            UseBrandVoice: true,
            new BrandGuideSelection(model.GuideId, model.VersionNumber),
            Audience: null,
            SourceDocumentIds: []);

        var assembled = await brandContext.AssembleAsync(
            selection.ToRequest(AiTaskType.BrandStyleTestDrive)!, cancellationToken);

        if (!assembled.Succeeded)
        {
            // One answer for every cause, including a guide belonging to another workspace (tenancy.md).
            return Refuse(
                AiBrandStyleTestDriveRequestErrors.GuideNotFound,
                "That version of that brand guide does not exist.");
        }

        if (assembled.Value!.Guidance.Count == 0 && assembled.Value.Rules.Count == 0)
        {
            return Refuse(
                AiBrandStyleTestDriveRequestErrors.GuideHasNoGuidance,
                "That version has nothing written in it yet, so there is nothing to try out. "
                    + "Write a part of your guide first.");
        }

        var now = clock.UtcNow;

        var requested = await operations.RequestAsync(
            new AiOperation
            {
                WorkspaceId = workspace.WorkspaceId,
                TaskType = AiTaskType.BrandStyleTestDrive,

                // Fixed here, never taken from the request: NotApplicable is what makes "this proposes no change
                // to anything" a property of the stored row rather than a claim about the handler.
                Scope = AiOperationScope.NotApplicable,
                Status = AiOperationStatus.Requested,

                // A brand guide is not a recipe, so neither is set — as for a brand guide proposal.
                RecipeId = null,
                RecipeVersionId = null,
                TaskInputsJson = SerializeInputs(model),
                IdempotencyKey = idempotencyKey,
                RequestedByMembershipId = workspace.MembershipId,
                RequestedAt = now,
                StatusChangedAt = now,
                AvailableAt = now,
            },
            cancellationToken);

        if (requested.Outcome is AiOperationRequestOutcome.KeyReusedForDifferentRequest)
        {
            return Refuse(
                IdempotencyPolicy.KeyReusedCode,
                "That idempotency key was already used for a different request.");
        }

        return new IdempotentOutcome<BrandStyleTestDriveServiceModel>(
            OperationResult<BrandStyleTestDriveServiceModel>.Success(Describe(requested.Operation!, null, null)),
            Replayed: requested.Outcome is AiOperationRequestOutcome.Replayed);
    }

    public async Task<OperationResult<BrandStyleTestDriveServiceModel>> GetAsync(
        Guid requestId, CancellationToken cancellationToken)
    {
        var found = await operations.GetWithProposalAsync(requestId, cancellationToken);

        // One absence for three conditions: no such request, one in another workspace, and one that ran a
        // different task.
        if (found is null || found.Operation.TaskType != AiTaskType.BrandStyleTestDrive)
        {
            return OperationResult<BrandStyleTestDriveServiceModel>.Failure(new OperationError(
                AiBrandStyleTestDriveRequestErrors.RequestNotFound,
                "That test drive does not exist.",
                new Dictionary<string, string[]>()));
        }

        if (found.Proposal is not { BrandContext: { } recorded } proposal)
        {
            return OperationResult<BrandStyleTestDriveServiceModel>.Success(
                Describe(found.Operation, null, null));
        }

        var comparison = await CompareAsync(found.Operation, proposal, recorded, cancellationToken);

        return OperationResult<BrandStyleTestDriveServiceModel>.Success(
            Describe(found.Operation, proposal, comparison));
    }

    /// <summary>
    /// The two columns, what the guide asks for, and what was cited.
    /// </summary>
    /// <remarks>
    /// The samples come from the stored rows and are never recomputed. Only the rule labels are re-derived, and
    /// <c>groundingChangedSince</c> is what tells a reader when those describe a guide that has moved on.
    /// </remarks>
    private async Task<BrandStyleTestDriveComparisonServiceModel> CompareAsync(
        AiOperation operation,
        AiProposal proposal,
        AiProposalBrandContext recorded,
        CancellationToken cancellationToken)
    {
        var byVariant = proposal.Changes
            .Where(change => change.TargetKind
                is AiChangeTargetKind.BrandStyleSampleWithoutGuide
                or AiChangeTargetKind.BrandStyleSampleWithGuide)
            .ToDictionary(
                change => (change.FieldName ?? string.Empty, Guided: IsGuided(change.TargetKind)),
                change => change);

        var samples = new List<BrandStyleSamplePairServiceModel>();

        foreach (var sample in AiBrandStyleSampleCatalog.All)
        {
            var name = AiBrandStyleSampleCatalog.ToWire(sample);
            var plain = byVariant.GetValueOrDefault((name, false));
            var guided = byVariant.GetValueOrDefault((name, true));

            if (plain is null || guided is null)
            {
                // A half-stored comparison is not reachable — the handler stores six rows or none — so it is
                // left out rather than rendered as an empty column somebody might read as the model's answer.
                continue;
            }

            samples.Add(new BrandStyleSamplePairServiceModel(
                name,
                plain.AfterValue ?? string.Empty,
                guided.AfterValue ?? string.Empty,
                [.. proposal.Warnings
                    .Where(warning => warning.AiStructuredChangeId == plain.Id
                        || warning.AiStructuredChangeId == guided.Id)
                    .OrderBy(warning => warning.SortOrder)
                    .Select(warning => new BrandStyleSampleNoteServiceModel(
                        warning.AiStructuredChangeId == guided.Id, warning.Kind, warning.Message))]));
        }

        var fresh = recorded.BrandGuideId is { } guideId && recorded.BrandGuideVersionNumber is { } versionNumber
            ? await ReassembleAsync(guideId, versionNumber, cancellationToken)
            : null;

        return new BrandStyleTestDriveComparisonServiceModel(
            BrandStyleTestDriveInputs.ReadSubject(ReadInputs(operation.TaskInputsJson)),
            recorded.BrandGuideId ?? Guid.Empty,
            recorded.BrandGuideVersionNumber ?? 0,
            recorded.GuideWasActiveVersion,
            proposal.ModelName,
            proposal.PromptTemplateVersion,
            proposal.CreatedAt,
            samples,
            fresh is null ? [] : Rules(fresh),
            await CitationsAsync(recorded, cancellationToken),
            [.. proposal.Warnings
                .Where(warning => warning.AiStructuredChangeId is null)
                .OrderBy(warning => warning.SortOrder)
                .Select(warning => new AiProposalWarningServiceModel(warning.Kind, warning.Message, null))],

            // Unreadable now and changed since are reported the same way, deliberately: both mean the rules
            // beside the samples cannot be shown to be the ones that produced them.
            fresh is null || !string.Equals(fresh.Checksum, recorded.Checksum, StringComparison.Ordinal));
    }

    private async Task<BrandContextPackage?> ReassembleAsync(
        Guid guideId, int versionNumber, CancellationToken cancellationToken)
    {
        var selection = new BrandContextRequestSelection(
            UseBrandVoice: true,
            new BrandGuideSelection(guideId, versionNumber),
            Audience: null,
            SourceDocumentIds: []);

        var assembled = await brandContext.AssembleAsync(
            selection.ToRequest(AiTaskType.BrandStyleTestDrive)!, cancellationToken);

        return assembled.Succeeded ? assembled.Value : null;
    }

    /// <summary>
    /// What the guide asks for, in the creator's own words — the same projection the writing preview publishes,
    /// so a creator sees one vocabulary whichever screen they are on.
    /// </summary>
    private static List<BrandWritingGuideRuleServiceModel> Rules(BrandContextPackage package)
    {
        var rules = package.Guidance
            .Select(item => new BrandWritingGuideRuleServiceModel(
                BrandWritingGuideText.Label(item.SectionKey),
                BrandWritingGuideText.Summarise(item.Body)))
            .ToList();

        rules.AddRange(package.Rules
            .Take(MaxRules)
            .Select(rule => new BrandWritingGuideRuleServiceModel(
                rule.Kind == BrandStyleGuideRuleKind.Do ? "Do" : "Don't",
                BrandWritingGuideText.Summarise(rule.Text))));

        return rules;
    }

    /// <summary>
    /// The cited passages, with their documents' current titles. A document the creator has since removed
    /// resolves to no title, and the citation still names what was used.
    /// </summary>
    private async Task<List<BrandStyleCitationServiceModel>> CitationsAsync(
        AiProposalBrandContext recorded, CancellationToken cancellationToken)
    {
        var titles = new Dictionary<Guid, string?>();

        foreach (var documentId in recorded.Sources.Select(source => source.BrandSourceDocumentId).Distinct())
        {
            var document = await documents.GetAsync(documentId, cancellationToken);

            titles[documentId] = document.Succeeded ? document.Value!.Title : null;
        }

        return [.. recorded.Sources
            .OrderBy(source => source.SortOrder)
            .Select(source => new BrandStyleCitationServiceModel(
                source.BrandSourceDocumentId,
                titles.GetValueOrDefault(source.BrandSourceDocumentId),
                source.DocumentVersionNumber,
                source.BrandSourcePassageId,
                source.Ordinal))];
    }

    private static bool IsGuided(AiChangeTargetKind kind) =>
        kind is AiChangeTargetKind.BrandStyleSampleWithGuide;

    private static BrandStyleTestDriveServiceModel Describe(
        AiOperation operation,
        AiProposal? proposal,
        BrandStyleTestDriveComparisonServiceModel? comparison) =>
        new(
            operation.Id,
            operation.Status,
            operation.FailureCategory,
            operation.RequestedAt,
            operation.StatusChangedAt,
            proposal is null ? null : comparison);

    /// <summary>
    /// The guide, the version and the subject, as the worker will read them back.
    /// </summary>
    /// <remarks>
    /// The guide and version travel through <see cref="BrandContextRequestInputs"/>, which every brand-grounded
    /// task shares, so the handler reads them with the same code every other one does. Only the subject is this
    /// capability's own key — and it is written only when the creator named one, so the stored inputs say what
    /// they chose rather than what the product defaulted to on the day they asked.
    /// </remarks>
    private static string SerializeInputs(RequestBrandStyleTestDriveViewModel model)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        BrandContextRequestInputs.Write(
            values,
            new BrandContextRequestSelection(
                UseBrandVoice: true,
                new BrandGuideSelection(model.GuideId, model.VersionNumber),
                Audience: null,
                SourceDocumentIds: []));

        if (!string.IsNullOrWhiteSpace(model.Subject))
        {
            values[BrandStyleTestDriveInputs.Subject] = model.Subject.Trim();
        }

        return JsonSerializer.Serialize(values);
    }

    private static IReadOnlyDictionary<string, string>? ReadInputs(string? inputsJson)
    {
        if (string.IsNullOrWhiteSpace(inputsJson))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(inputsJson);
        }
        catch (JsonException)
        {
            // A value this server wrote that cannot be read back is a defect, not bad creator input — and the
            // samples are still readable, so the subject falls back to the platform's rather than failing a read.
            return null;
        }
    }

    private static IdempotentOutcome<BrandStyleTestDriveServiceModel> Refuse(string code, string message) =>
        new(
            OperationResult<BrandStyleTestDriveServiceModel>.Failure(
                new OperationError(code, message, new Dictionary<string, string[]>())),
            Replayed: false);
}

using System.Globalization;
using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Content.Data;
using CreatorPantry.Domain.Modules.Content.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Managers;

namespace CreatorPantry.Domain.Modules.Content.Business;

/// <summary>The rules of a long-form draft and its plan (LF.2.2, baseline B-33).</summary>
public interface ILongFormDraftBusiness
{
    /// <summary>
    /// The draft with this id, created for the context and kind if it does not exist yet.
    /// </summary>
    /// <remarks>
    /// Idempotent on the id, which the caller chooses: the request that starts a draft and the plan that later
    /// lands on it both name it, and whichever arrives first creates it.
    /// </remarks>
    Task<OperationResult<LongFormDraftServiceModel>> EnsureStartedAsync(
        Guid draftId, Guid creativeContextId, LongFormKind kind, CancellationToken cancellationToken);

    Task<OperationResult<LongFormDraftServiceModel>> GetAsync(Guid draftId, CancellationToken cancellationToken);

    Task<CursorPageServiceModel<LongFormDraftSummaryServiceModel>> ListRecentAsync(
        LongFormDraftListCriteria criteria, CancellationToken cancellationToken);

    /// <summary>Stores a generated plan as the draft's next revision. Idempotent on the proposal.</summary>
    Task<OperationResult<LongFormDraftServiceModel>> RecordGeneratedPlanAsync(
        LongFormGeneratedPlanInput input, CancellationToken cancellationToken);

    /// <summary>Saves the creator's own plan as a new revision.</summary>
    Task<OperationResult<LongFormDraftServiceModel>> PatchPlanAsync(
        string actorUserId, Guid draftId, PatchLongFormPlanViewModel model, CancellationToken cancellationToken);

    /// <summary>Approves the latest plan, so its sections may be written.</summary>
    Task<OperationResult<LongFormDraftServiceModel>> ApprovePlanAsync(
        string actorUserId, Guid draftId, ApproveLongFormPlanViewModel model, CancellationToken cancellationToken);
}

/// <inheritdoc cref="ILongFormDraftBusiness"/>
/// <remarks>
/// <para>
/// <strong>A draft is a slot and a revision is a state.</strong> Nothing here edits a revision: a generated
/// plan and a creator's edit are each a new one, numbered after the last, carrying forward the sources the
/// plan was written against. The draft row holds only where the work stands.
/// </para>
/// <para>
/// <strong>Saving is not approving.</strong> A creator may save a plan that is not yet valid — a heading still
/// blank, a role used twice while they rearrange — and is shown what stands in the way. Only approval is held
/// to the shape rules, and it says which sections fail.
/// </para>
/// <para>
/// <strong>The workspace is never an input.</strong> Every read goes through the query filter of the resolved
/// context, so another workspace's draft, context or revision is not found rather than found and refused.
/// </para>
/// </remarks>
internal sealed class LongFormDraftBusiness(
    ILongFormDraftDataLayer dataLayer,
    IWorkspaceContext workspace,
    IClock clock) : ILongFormDraftBusiness
{
    public async Task<OperationResult<LongFormDraftServiceModel>> EnsureStartedAsync(
        Guid draftId, Guid creativeContextId, LongFormKind kind, CancellationToken cancellationToken)
    {
        if (await dataLayer.FindAsync(draftId, cancellationToken) is { } existing)
        {
            // The same id for a different piece is not a retry of anything.
            return existing.CreativeContextId == creativeContextId && existing.Kind == kind
                ? await DescribeAsync(existing, cancellationToken)
                : Failure(ContextRefused());
        }

        if (!await dataLayer.ContextExistsAsync(creativeContextId, cancellationToken))
        {
            return Failure(ContextRefused());
        }

        var now = clock.UtcNow;
        var draft = new LongFormDraft
        {
            Id = draftId,
            CreativeContextId = creativeContextId,
            Kind = kind,
            Status = LongFormDraftStatus.Draft,
            CreatedByMembershipId = workspace.MembershipId,
            CreatedAt = now,
            UpdatedAt = now,
        };

        if (await dataLayer.SaveAsync(draft, isNew: true, revision: null, [], audit: null, cancellationToken)
            is LongFormDraftWrite.Saved)
        {
            return await DescribeAsync(draft, cancellationToken);
        }

        // Refused: the other caller that names this id got there first, which is the outcome asked for.
        return await dataLayer.FindAsync(draftId, cancellationToken) is { } raced
            ? await DescribeAsync(raced, cancellationToken)
            : Failure(ContextRefused());
    }

    public async Task<OperationResult<LongFormDraftServiceModel>> GetAsync(Guid draftId, CancellationToken cancellationToken) =>
        await dataLayer.FindAsync(draftId, cancellationToken) is { } draft
            ? await DescribeAsync(draft, cancellationToken)
            : Failure(NotFound());

    public async Task<CursorPageServiceModel<LongFormDraftSummaryServiceModel>> ListRecentAsync(
        LongFormDraftListCriteria criteria, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(criteria);

        var (rows, hasMore) = await dataLayer.ListRecentAsync(criteria, cancellationToken);

        return PageBuilder.Build(rows, hasMore, criteria.Scope, row => new LongFormDraftSummaryServiceModel(
            row.Id, row.CreativeContextId, row.Kind, row.Status, row.LatestRevisionNumber, row.UpdatedAt));
    }

    public async Task<OperationResult<LongFormDraftServiceModel>> RecordGeneratedPlanAsync(
        LongFormGeneratedPlanInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        var ensured = await EnsureStartedAsync(input.DraftId, input.CreativeContextId, input.Kind, cancellationToken);

        if (!ensured.Succeeded)
        {
            return ensured;
        }

        // Landing a proposal is idempotent, and this is where that holds: the worker lands a proposal as soon
        // as it commits and the maintenance sweep lands it again for a while afterwards.
        if (await dataLayer.HasRevisionForProposalAsync(input.DraftId, input.AiProposalId, cancellationToken))
        {
            return ensured;
        }

        var draft = (await dataLayer.FindForUpdateAsync(input.DraftId, cancellationToken))!;

        if (!PlanIsOpen(draft))
        {
            return Failure(Locked());
        }

        var referenced = input.RecipePins.Select(pin => pin.CreativeContextReferenceId).ToHashSet();

        // An item's source is one of the recipes this very plan was written against, or it is nothing: the id
        // is kept in the document with no key behind it, so this is the only place it can be held to anything.
        if (input.Sections.Count == 0
            || input.Sections.Any(section => section.SourceReferenceId is { } source && !referenced.Contains(source)))
        {
            return Failure(SourceRefused());
        }

        var latest = await dataLayer.FindLatestRevisionAsync(draft.Id, cancellationToken);
        var now = clock.UtcNow;

        var revision = NewRevision(
            draft,
            latest,
            ContentRevisionSource.AiGenerated,
            new LongFormDocument(
            [
                .. input.Sections.Select(section => new LongFormSection(
                    Guid.NewGuid(), section.Role, section.Heading, section.Intent, Body: null, Settled: false,
                    section.SourceReferenceId, Provenance: null)),
            ]),
            now);

        revision.AiProposalId = input.AiProposalId;
        revision.PromptTemplateId = input.PromptTemplateId;
        revision.PromptTemplateVersion = input.PromptTemplateVersion;
        revision.PromptTemplateBodyChecksum = input.PromptTemplateBodyChecksum;
        revision.BrandStyleGuideVersionId = input.BrandStyleGuideVersionId;
        revision.CreativeContextVersion = input.CreativeContextVersion;

        var pins = input.RecipePins
            .Select((pin, index) => new LongFormRevisionRecipePin
            {
                Id = Guid.NewGuid(),
                LongFormRevisionId = revision.Id,
                SortOrder = index,
                RecipeId = pin.RecipeId,
                RecipeVersionId = pin.RecipeVersionId,
                CreativeContextReferenceId = pin.CreativeContextReferenceId,
            })
            .ToList();

        // A new plan is a plan nobody has approved, whatever stood before it.
        draft.Status = LongFormDraftStatus.Draft;
        draft.UpdatedAt = now;

        // A refused row is a pin the keys would not take — a recipe version or a proposal this workspace does
        // not hold — and the caller is not told which.
        return await SaveAsync(draft, revision, pins, audit: null, onRefused: SourceRefused(), cancellationToken);
    }

    public async Task<OperationResult<LongFormDraftServiceModel>> PatchPlanAsync(
        string actorUserId, Guid draftId, PatchLongFormPlanViewModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        var draft = await dataLayer.FindForUpdateAsync(draftId, cancellationToken);

        if (draft is null)
        {
            return Failure(NotFound());
        }

        if (!CreativeContextConcurrencyToken.Matches(model.ExpectedConcurrencyToken, draft.RowVersion))
        {
            return Failure(Stale());
        }

        if (!PlanIsOpen(draft))
        {
            return Failure(Locked());
        }

        var latest = await dataLayer.FindLatestRevisionAsync(draft.Id, cancellationToken);

        if (latest is null)
        {
            return Failure(NotStarted());
        }

        var current = LongFormDocument.Deserialize(latest.Content);
        var known = current.Sections.ToDictionary(section => section.Id);
        var parentPins = await dataLayer.ListPinsAsync(latest.Id, cancellationToken);
        var referenced = parentPins.Select(pin => pin.CreativeContextReferenceId).ToHashSet();

        var failures = new List<(string, string)>();
        var sections = new List<LongFormSection>(model.Sections!.Count);

        for (var index = 0; index < model.Sections.Count; index++)
        {
            var submitted = model.Sections[index];
            var role = submitted.Role!.Value;
            LongFormSection? existing = null;

            // An id names a section this plan already has. One it does not have names nothing here — and is
            // refused rather than adopted, so a section cannot be smuggled in from another draft by its id.
            if (submitted.Id is { } id && !known.TryGetValue(id, out existing))
            {
                failures.Add(($"sections[{Number(index)}].id", "That is not a section of this plan. Leave the id out to add one."));
                continue;
            }

            // Only an item covers a source, and only one this plan was written against.
            if (submitted.SourceReferenceId is { } source
                && (role is not LongFormSectionRole.Item || !referenced.Contains(source)))
            {
                failures.Add((
                    $"sections[{Number(index)}].sourceReferenceId",
                    "Only an item names a source, and only one of the recipes this plan was written for."));
                continue;
            }

            sections.Add(new LongFormSection(
                existing?.Id ?? Guid.NewGuid(),
                role,
                submitted.Heading!,
                submitted.Intent!,
                existing?.Body,
                existing?.Settled ?? false,
                submitted.SourceReferenceId,
                existing?.Provenance));
        }

        if (failures.Count > 0)
        {
            return Failure(OperationError.Validation(
                ContentErrorCodes.LongFormPlanInvalid, "That plan could not be saved as described.", failures));
        }

        // Saving the plan as it already stands is not an edit: no revision, no audit entry, no moved token.
        if (current.Sections.SequenceEqual(sections))
        {
            return await DescribeAsync(draft, cancellationToken);
        }

        var now = clock.UtcNow;
        var revision = NewRevision(draft, latest, ContentRevisionSource.CreatorEdit, new LongFormDocument(sections), now);

        // An edit is written against what its parent was written against: the sources did not change because
        // the creator renamed a heading. Carried forward whole, so staleness still has a pin to compare.
        revision.BrandProfileRevisionId = latest.BrandProfileRevisionId;
        revision.BrandStyleGuideVersionId = latest.BrandStyleGuideVersionId;
        revision.PromptTemplateId = latest.PromptTemplateId;
        revision.PromptTemplateVersion = latest.PromptTemplateVersion;
        revision.PromptTemplateBodyChecksum = latest.PromptTemplateBodyChecksum;
        revision.CreativeContextVersion = latest.CreativeContextVersion;

        var pins = parentPins
            .Select(pin => new LongFormRevisionRecipePin
            {
                Id = Guid.NewGuid(),
                LongFormRevisionId = revision.Id,
                SortOrder = pin.SortOrder,
                RecipeId = pin.RecipeId,
                RecipeVersionId = pin.RecipeVersionId,
                CreativeContextReferenceId = pin.CreativeContextReferenceId,
            })
            .ToList();

        var before = draft.Status;

        // An approved plan that has been changed is a plan nobody has approved.
        draft.Status = LongFormDraftStatus.Draft;
        draft.UpdatedAt = now;

        return await SaveAsync(
            draft,
            revision,
            pins,
            Audit(
                actorUserId,
                ContentAuditActions.LongFormPlanEdited,
                draft,
                $"Saved revision {Number(revision.RevisionNumber)} of a long-form plan.",
                before),
            onRefused: Stale(),
            cancellationToken);
    }

    public async Task<OperationResult<LongFormDraftServiceModel>> ApprovePlanAsync(
        string actorUserId, Guid draftId, ApproveLongFormPlanViewModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        var draft = await dataLayer.FindForUpdateAsync(draftId, cancellationToken);

        if (draft is null)
        {
            return Failure(NotFound());
        }

        var latest = await dataLayer.FindLatestRevisionAsync(draft.Id, cancellationToken);

        // Approving what is already approved changes nothing and is not an error — checked before the token,
        // because the first approval moved it, and a retry after a lost response still holds the old one.
        if (draft.Status is LongFormDraftStatus.Outlined && latest?.RevisionNumber == model.RevisionNumber)
        {
            return await DescribeAsync(draft, cancellationToken);
        }

        if (!CreativeContextConcurrencyToken.Matches(model.ExpectedConcurrencyToken, draft.RowVersion))
        {
            return Failure(Stale());
        }

        if (!PlanIsOpen(draft))
        {
            return Failure(Locked());
        }

        if (latest is null)
        {
            return Failure(NotStarted());
        }

        // The approval names what was read. A plan edited since is a different plan.
        if (latest.RevisionNumber != model.RevisionNumber)
        {
            return Failure(Stale());
        }

        var document = LongFormDocument.Deserialize(latest.Content);
        var failures = LongFormDocumentValidator.Validate(draft.Kind, document).Findings
            .Where(finding => finding.Severity is LongFormFindingSeverity.Failure)
            .ToList();

        if (failures.Count > 0)
        {
            var positions = document.Sections.Select((section, index) => (section.Id, index)).ToDictionary(item => item.Id, item => item.index);

            return Failure(OperationError.Validation(
                ContentErrorCodes.LongFormPlanUnprocessable,
                "This plan is not ready to approve.",
                failures.Select(finding => (
                    finding.SectionId is { } id ? $"sections[{Number(positions[id])}]" : "sections",
                    Explain(finding.Code)))));
        }

        var before = draft.Status;
        draft.Status = LongFormDraftStatus.Outlined;
        draft.UpdatedAt = clock.UtcNow;

        return await SaveAsync(
            draft,
            revision: null,
            [],
            Audit(
                actorUserId,
                ContentAuditActions.LongFormPlanApproved,
                draft,
                $"Approved revision {Number(latest.RevisionNumber)} of a long-form plan.",
                before),
            onRefused: Stale(),
            cancellationToken);
    }

    /// <summary>A plan can be changed until writing has begun; after that the sections are the work.</summary>
    private static bool PlanIsOpen(LongFormDraft draft) =>
        draft.Status is LongFormDraftStatus.Draft or LongFormDraftStatus.Outlined;

    private LongFormRevision NewRevision(
        LongFormDraft draft, LongFormRevision? parent, ContentRevisionSource source, LongFormDocument document, DateTimeOffset now) => new()
        {
            Id = Guid.NewGuid(),
            LongFormDraftId = draft.Id,
            RevisionNumber = (parent?.RevisionNumber ?? 0) + 1,
            ParentRevisionId = parent?.Id,
            Source = source,
            SchemaVersion = LongFormDocument.CurrentSchemaVersion,
            Content = document.Serialize(),
            CreatedByMembershipId = workspace.MembershipId,
            CreatedAt = now,
        };

    private async Task<OperationResult<LongFormDraftServiceModel>> SaveAsync(
        LongFormDraft draft,
        LongFormRevision? revision,
        IReadOnlyList<LongFormRevisionRecipePin> pins,
        AuditEntry? audit,
        OperationError onRefused,
        CancellationToken cancellationToken) =>
        await dataLayer.SaveAsync(draft, isNew: false, revision, pins, audit, cancellationToken) switch
        {
            LongFormDraftWrite.Saved => await DescribeAsync(draft, cancellationToken),
            LongFormDraftWrite.Stale => Failure(Stale()),
            _ => Failure(onRefused),
        };

    /// <summary>The draft with its latest revision, read after the write so the token is the one now in force.</summary>
    private async Task<OperationResult<LongFormDraftServiceModel>> DescribeAsync(
        LongFormDraft draft, CancellationToken cancellationToken)
    {
        var latest = await dataLayer.FindLatestRevisionAsync(draft.Id, cancellationToken);

        if (latest is null)
        {
            return OperationResult<LongFormDraftServiceModel>.Success(ToServiceModel(draft, null, null));
        }

        var document = LongFormDocument.Deserialize(latest.Content);
        var checkedPlan = LongFormDocumentValidator.Validate(draft.Kind, document);

        // Failures only. Ceilings and word targets are about bodies, and a plan read carries none.
        var findings = checkedPlan.Findings
            .Where(finding => finding.Severity is LongFormFindingSeverity.Failure)
            .Select(finding => new LongFormPlanCheckFindingServiceModel(finding.Code, finding.SectionId))
            .ToList();

        return OperationResult<LongFormDraftServiceModel>.Success(ToServiceModel(
            draft,
            new LongFormRevisionServiceModel(
                latest.RevisionNumber,
                latest.Source,
                latest.CreatedAt,
                [
                    .. document.Sections.Select(section => new LongFormPlannedSectionServiceModel(
                        section.Id,
                        section.Role,
                        section.Heading,
                        section.Intent,
                        !string.IsNullOrWhiteSpace(section.Body),
                        section.Settled,
                        section.SourceReferenceId)),
                ]),
            new LongFormPlanCheckServiceModel(findings.Count == 0, checkedPlan.PolicyVersion, findings)));
    }

    private static LongFormDraftServiceModel ToServiceModel(
        LongFormDraft draft, LongFormRevisionServiceModel? latest, LongFormPlanCheckServiceModel? check) => new(
        draft.Id,
        draft.CreativeContextId,
        draft.Kind,
        draft.Status,
        CreativeContextConcurrencyToken.From(draft.RowVersion),
        draft.CreatedAt,
        draft.UpdatedAt,
        latest,
        check);

    private static string Explain(string code) => code switch
    {
        LongFormFindingCodes.NoSections => "A plan needs at least one section.",
        LongFormFindingCodes.DuplicateSectionId => "This section appears in the plan twice.",
        LongFormFindingCodes.UnknownRole => "This section's role is not one a plan can have.",
        LongFormFindingCodes.RoleNotAllowed => "This kind of piece does not have a section of this role.",
        LongFormFindingCodes.RoleRepeated => "This role appears more often than it may.",
        LongFormFindingCodes.HeadingBlank => "Give this section a heading.",
        LongFormFindingCodes.HeadingTooLong => $"Keep this heading to {Number(LongFormPolicy.HeadingMaxCharacters)} characters.",
        LongFormFindingCodes.IntentBlank => "Say in one line what this section is for.",
        LongFormFindingCodes.IntentNotOneLine => "Keep this section's intent to one line.",
        LongFormFindingCodes.IntentTooLong => $"Keep this intent to {Number(LongFormPolicy.IntentMaxCharacters)} characters.",
        _ => "This section does not meet the rules for a plan.",
    };

    // State references only: a revision number and statuses — never a word of the plan.
    private static AuditEntry Audit(
        string actorUserId, string action, LongFormDraft draft, string summary, LongFormDraftStatus before) => new(
        actorUserId,
        action,
        ContentAuditActions.LongFormDraftResourceType,
        draft.Id.ToString("D"),
        CorrelationId(),
        summary,
        BeforeReference: before.ToString(),
        AfterReference: draft.Status.ToString());

    private static Guid CorrelationId()
    {
        // A W3C trace id is sixteen bytes, the same width as a Guid, so an operator can paste the audit row's
        // correlation id into a trace search and find the request.
        var traceId = System.Diagnostics.Activity.Current?.TraceId;

        return traceId is { } id && id != default ? Guid.ParseExact(id.ToHexString(), "N") : Guid.NewGuid();
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static OperationResult<LongFormDraftServiceModel> Failure(OperationError error) =>
        OperationResult<LongFormDraftServiceModel>.Failure(error);

    private static OperationError Plain(string code, string message) =>
        new(code, message, new Dictionary<string, string[]>());

    private static OperationError NotFound() =>
        Plain(ContentErrorCodes.LongFormDraftNotFound, "That long-form draft does not exist.");

    private static OperationError Stale() => Plain(
        ContentErrorCodes.LongFormDraftStale,
        "This draft has changed since you opened it. Reload it and make your change again.");

    private static OperationError Locked() => Plain(
        ContentErrorCodes.LongFormPlanLocked,
        "This draft's sections are already being written, so its plan can no longer be changed.");

    private static OperationError NotStarted() => Plain(
        ContentErrorCodes.LongFormPlanNotStarted, "This draft has no plan yet. Ask for an outline first.");

    private static OperationError ContextRefused() => Plain(
        ContentErrorCodes.LongFormContextUnprocessable, "That piece of work cannot be used for a long-form draft.");

    private static OperationError SourceRefused() => Plain(
        ContentErrorCodes.LongFormSourceUnprocessable, "That plan could not be stored against its sources.");
}

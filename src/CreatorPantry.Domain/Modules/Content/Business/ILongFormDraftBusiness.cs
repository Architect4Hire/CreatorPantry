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

    /// <summary>
    /// Puts a newsletter issue's items in the order named, by the reference each covers, as a new revision.
    /// </summary>
    Task<OperationResult<LongFormDraftServiceModel>> ReorderItemsAsync(
        string actorUserId, Guid draftId, ReorderLongFormItemsViewModel model, CancellationToken cancellationToken);

    /// <summary>Saves a newsletter issue's subject line and note as a new revision.</summary>
    Task<OperationResult<LongFormDraftServiceModel>> PutFramingAsync(
        string actorUserId, Guid draftId, PutLongFormFramingViewModel model, CancellationToken cancellationToken);
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
/// <para>
/// <strong>A newsletter item is the reference it covers (LF.4.1).</strong> An item already in a plan keeps its
/// reference for as long as it is in the plan, so its words can never end up under another recipe's name.
/// Taking an item out of the plan takes its recipe out of the issue: the new revision no longer pins it, and
/// the revision before still does. Items change order by reference, and that moves no words.
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
                ],

                // The subject and the note are the creator's and were never the outline's to propose, so a
                // plan proposed afresh does not cost them.
                latest is null ? null : LongFormDocument.Deserialize(latest.Content).Framing),
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

            // An item is the reference it covers. Pointing one that is already in the plan at another recipe —
            // or at none — would leave whatever was written for the first under the second's name.
            if (existing?.SourceReferenceId is { } bound && submitted.SourceReferenceId != bound)
            {
                failures.Add((
                    $"sections[{Number(index)}].sourceReferenceId",
                    "This item already covers a recipe, and keeps it. Remove the item to take that recipe out of the piece."));
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
                existing?.Provenance,
                existing?.Findings));
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
        // An issue covers the recipes its items cover. An item taken out of the plan takes its recipe out of
        // the issue, so this revision does not pin it — the one before still does, and is not touched. Every
        // other kind of piece keeps every pin: its recipes are not tied to any one section.
        var covered = sections
            .Where(section => section.Role is LongFormSectionRole.Item)
            .Select(section => section.SourceReferenceId)
            .ToHashSet();

        var (revision, pins) = NewEdit(
            draft,
            latest,
            current with { Sections = sections },
            draft.Kind is LongFormKind.NewsletterIssue
                ? [.. parentPins.Where(pin => covered.Contains(pin.CreativeContextReferenceId))]
                : parentPins,
            now);

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
        var failures = LongFormDocumentValidator
            .Validate(draft.Kind, document, await BoundReferencesAsync(latest, cancellationToken)).Findings
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

    public async Task<OperationResult<LongFormDraftServiceModel>> ReorderItemsAsync(
        string actorUserId, Guid draftId, ReorderLongFormItemsViewModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        var opened = await OpenIssueAsync(draftId, model.ExpectedConcurrencyToken, cancellationToken);

        if (opened.Refused is { } refused)
        {
            return Failure(refused);
        }

        var (draft, latest, document) = opened.Found!.Value;
        var order = model.SourceReferenceIds!;
        var bySource = new Dictionary<Guid, LongFormSection>();
        var items = document.Sections.Where(section => section.Role is LongFormSectionRole.Item).ToList();

        // Exactly the items the issue has, each once. One answer for an id that is not an item's, an item left
        // out and an issue whose items are not yet bound, so the refusal says nothing about what any id names.
        if (items.Any(item => item.SourceReferenceId is not { } source || !bySource.TryAdd(source, item))
            || order.Count != items.Count
            || order.Any(source => !bySource.ContainsKey(source)))
        {
            return Failure(OperationError.Validation(
                ContentErrorCodes.LongFormItemOrderInvalid,
                "That order could not be kept.",
                [("sourceReferenceIds", "Name each of this issue's items once, by the reference it covers.")]));
        }

        if (order.SequenceEqual(items.Select(item => item.SourceReferenceId!.Value)))
        {
            return await DescribeAsync(draft, cancellationToken);
        }

        // The places items hold among the other sections stay where they are; which item is in each changes.
        // Every section is carried whole — heading, text, settled mark, provenance — so no word moves.
        var next = 0;
        var reordered = document with
        {
            Sections =
            [
                .. document.Sections.Select(section =>
                    section.Role is LongFormSectionRole.Item ? bySource[order[next++]] : section),
            ],
        };

        var now = clock.UtcNow;
        var (revision, pins) = NewEdit(
            draft, latest, reordered, await dataLayer.ListPinsAsync(latest.Id, cancellationToken), now);

        // The status stands. What was approved is which sections the piece has and what each covers, and an
        // order of items changes neither; a piece being written goes on being written.
        draft.UpdatedAt = now;

        return await SaveAsync(
            draft,
            revision,
            pins,
            Audit(
                actorUserId,
                ContentAuditActions.LongFormItemsReordered,
                draft,
                $"Saved revision {Number(revision.RevisionNumber)} of a newsletter issue with its items in a new order.",
                draft.Status),
            onRefused: Stale(),
            cancellationToken);
    }

    public async Task<OperationResult<LongFormDraftServiceModel>> PutFramingAsync(
        string actorUserId, Guid draftId, PutLongFormFramingViewModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        var opened = await OpenIssueAsync(draftId, model.ExpectedConcurrencyToken, cancellationToken);

        if (opened.Refused is { } refused)
        {
            return Failure(refused);
        }

        var (draft, latest, document) = opened.Found!.Value;

        // Kept as typed. Blank is "none", and an issue with neither part has no framing at all.
        var subject = string.IsNullOrWhiteSpace(model.Subject) ? null : model.Subject;
        var note = string.IsNullOrWhiteSpace(model.Note) ? null : model.Note;
        var framing = subject is null && note is null ? null : new LongFormIssueFraming(subject, note);

        if (framing == document.Framing)
        {
            return await DescribeAsync(draft, cancellationToken);
        }

        var now = clock.UtcNow;
        var (revision, pins) = NewEdit(
            draft,
            latest,
            document with { Framing = framing },
            await dataLayer.ListPinsAsync(latest.Id, cancellationToken),
            now);

        // Not a change to the plan: the sections, and so their approval, are as they were.
        draft.UpdatedAt = now;

        return await SaveAsync(
            draft,
            revision,
            pins,
            Audit(
                actorUserId,
                ContentAuditActions.LongFormFramingEdited,
                draft,
                $"Saved revision {Number(revision.RevisionNumber)} of a newsletter issue with its subject or note changed.",
                draft.Status),
            onRefused: Stale(),
            cancellationToken);
    }

    /// <summary>A newsletter issue, its latest revision and that revision's document, for a creator's own write.</summary>
    private async Task<(OperationError? Refused, (LongFormDraft, LongFormRevision, LongFormDocument)? Found)> OpenIssueAsync(
        Guid draftId, string? expectedToken, CancellationToken cancellationToken)
    {
        var draft = await dataLayer.FindForUpdateAsync(draftId, cancellationToken);

        if (draft is null)
        {
            return (NotFound(), null);
        }

        if (!CreativeContextConcurrencyToken.Matches(expectedToken, draft.RowVersion))
        {
            return (Stale(), null);
        }

        if (draft.Kind is not LongFormKind.NewsletterIssue)
        {
            return (
                Plain(ContentErrorCodes.LongFormNotAnIssue, "Only a newsletter issue has items to order, a subject line and a note."),
                null);
        }

        // Until it is accepted. An accepted issue is the article of record, and nothing is written to it.
        if (draft.Status is not (LongFormDraftStatus.Draft or LongFormDraftStatus.Outlined or LongFormDraftStatus.Writing))
        {
            return (
                draft.Status is LongFormDraftStatus.Accepted
                    ? Plain(ContentErrorCodes.LongFormDraftAccepted, "This draft has been accepted, so it is no longer being changed.")
                    : Plain(ContentErrorCodes.LongFormDraftNotWritable, "This draft is not being changed at the moment."),
                null);
        }

        return await dataLayer.FindLatestRevisionAsync(draft.Id, cancellationToken) is { } latest
            ? (null, (draft, latest, LongFormDocument.Deserialize(latest.Content)))
            : (NotStarted(), null);
    }

    /// <summary>
    /// A creator's edit as the next revision, written against what its parent was written against: the sources
    /// did not change because the creator renamed a heading. The pins given are copied, so staleness still has
    /// one to compare.
    /// </summary>
    private (LongFormRevision Revision, List<LongFormRevisionRecipePin> Pins) NewEdit(
        LongFormDraft draft,
        LongFormRevision latest,
        LongFormDocument document,
        IReadOnlyList<LongFormRevisionRecipePin> carried,
        DateTimeOffset now)
    {
        var revision = NewRevision(draft, latest, ContentRevisionSource.CreatorEdit, document, now);

        revision.BrandProfileRevisionId = latest.BrandProfileRevisionId;
        revision.BrandStyleGuideVersionId = latest.BrandStyleGuideVersionId;
        revision.PromptTemplateId = latest.PromptTemplateId;
        revision.PromptTemplateVersion = latest.PromptTemplateVersion;
        revision.PromptTemplateBodyChecksum = latest.PromptTemplateBodyChecksum;
        revision.CreativeContextVersion = latest.CreativeContextVersion;

        return (
            revision,
            [
                .. carried.Select(pin => new LongFormRevisionRecipePin
                {
                    Id = Guid.NewGuid(),
                    LongFormRevisionId = revision.Id,
                    SortOrder = pin.SortOrder,
                    RecipeId = pin.RecipeId,
                    RecipeVersionId = pin.RecipeVersionId,
                    CreativeContextReferenceId = pin.CreativeContextReferenceId,
                }),
            ]);
    }

    /// <summary>The references a revision pinned: what a newsletter's items are held to.</summary>
    private async Task<IReadOnlyList<Guid>> BoundReferencesAsync(LongFormRevision revision, CancellationToken cancellationToken) =>
        [.. (await dataLayer.ListPinsAsync(revision.Id, cancellationToken)).Select(pin => pin.CreativeContextReferenceId)];

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
            return OperationResult<LongFormDraftServiceModel>.Success(ToServiceModel(draft, null, null, null, []));
        }

        var document = LongFormDocument.Deserialize(latest.Content);
        var pins = await dataLayer.ListPinsAsync(latest.Id, cancellationToken);
        var checkedPlan = LongFormDocumentValidator.Validate(
            draft.Kind, document, [.. pins.Select(pin => pin.CreativeContextReferenceId)]);

        // Read now, from the piece of work as it stands, and never part of the check above: a reference the
        // context has since dropped is something to tell the creator, not something wrong with the revision.
        var sources = await dataLayer.ReadItemSourcesAsync(draft, document, pins, cancellationToken);

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
                        section.SourceReferenceId,
                        sources.TryGetValue(section.Id, out var state) ? state : null)),
                ]),
            new LongFormPlanCheckServiceModel(findings.Count == 0, checkedPlan.PolicyVersion, findings),
            document.Framing is { } framing ? new LongFormFramingServiceModel(framing.Subject, framing.Note) : null,
            sources.Unresolved(document)));
    }

    private static LongFormDraftServiceModel ToServiceModel(
        LongFormDraft draft,
        LongFormRevisionServiceModel? latest,
        LongFormPlanCheckServiceModel? check,
        LongFormFramingServiceModel? framing,
        IReadOnlyList<Guid> unresolvedItems) => new(
        draft.Id,
        draft.CreativeContextId,
        draft.Kind,
        draft.Status,
        CreativeContextConcurrencyToken.From(draft.RowVersion),
        draft.CreatedAt,
        draft.UpdatedAt,
        latest,
        check,
        framing,
        unresolvedItems);

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
        LongFormFindingCodes.ItemSourceMissing => "This item does not say which of the piece's recipes it covers.",
        LongFormFindingCodes.ItemSourceUnknown => "This item covers a recipe this plan was not written for.",
        LongFormFindingCodes.ItemSourceRepeated => "Another item already covers this recipe.",
        LongFormFindingCodes.ItemsSourceUncovered => "One of the piece's recipes has no item. An issue has one item for each.",
        LongFormFindingCodes.SectionSourceNotAllowed => "Only an item covers a recipe.",
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

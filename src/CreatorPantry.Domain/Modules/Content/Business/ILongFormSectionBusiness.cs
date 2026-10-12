using System.Globalization;
using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Facade;
using CreatorPantry.Domain.Modules.Content.Data;
using CreatorPantry.Domain.Modules.Content.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Managers;

namespace CreatorPantry.Domain.Modules.Content.Business;

/// <summary>What the content module does with the sections of a long-form draft (LF.3.1, LF.3.3).</summary>
public interface ILongFormSectionBusiness
{
    /// <summary>
    /// The context one section is written in, or the one reason it cannot be written now.
    /// </summary>
    Task<OperationResult<LongFormWritingContextServiceModel>> GetWritingContextAsync(
        Guid draftId, int revisionNumber, Guid sectionId, CancellationToken cancellationToken);

    /// <summary>Every section of the draft's latest revision, with its body and where that body came from.</summary>
    Task<OperationResult<LongFormSectionsServiceModel>> GetSectionsAsync(Guid draftId, CancellationToken cancellationToken);

    /// <summary>
    /// The same, refused when the draft's sections may not be written now. Changes nothing.
    /// </summary>
    Task<OperationResult<LongFormSectionsServiceModel>> GetWritableSectionsAsync(Guid draftId, CancellationToken cancellationToken);

    /// <summary>
    /// Marks the draft as being written, which closes its plan. Changes nothing when it already is.
    /// </summary>
    Task<OperationResult<LongFormSectionsServiceModel>> BeginWritingAsync(Guid draftId, CancellationToken cancellationToken);

    /// <summary>Stores a generated body as the draft's next revision. Idempotent on the proposal.</summary>
    Task<OperationResult<LongFormSectionsServiceModel>> RecordGeneratedSectionAsync(
        LongFormGeneratedSectionBodyInput input, CancellationToken cancellationToken);

    /// <summary>Saves the creator's own text for one section as a new revision.</summary>
    Task<OperationResult<LongFormSectionsServiceModel>> EditSectionAsync(
        string actorUserId, Guid draftId, Guid sectionId, EditLongFormSectionViewModel model, CancellationToken cancellationToken);

    /// <summary>Sets or clears one section's settled flag, as a new revision.</summary>
    Task<OperationResult<LongFormSectionsServiceModel>> SettleSectionAsync(
        string actorUserId, Guid draftId, Guid sectionId, SettleLongFormSectionViewModel model, CancellationToken cancellationToken);
}

/// <inheritdoc />
/// <remarks>
/// <para>
/// <strong>One section moves; every other is copied.</strong> A generated body, a creator's edit and a settled
/// flag are each a new revision whose document is the last one with exactly one section changed. Nothing here
/// builds a document from a request: the other sections' text is never an input, so it cannot be changed by
/// one.
/// </para>
/// <para>
/// <strong>Stale is judged per section.</strong> A request to write a section names the revision it was made
/// against. What must not have happened since is a change to <em>that section's</em> text — the creator edited
/// it, or another run wrote it. Other sections landing in between is the ordinary course of a piece being
/// written and refuses nothing, which is what lets several sections be written side by side.
/// </para>
/// <para>
/// <strong>The plan is closed by the first act of writing.</strong> Asking for a section, or typing one,
/// moves an approved draft to <c>Writing</c>. From then on headings and order are fixed and only bodies move.
/// </para>
/// <para>
/// <strong>The workspace is never an input.</strong> Every read goes through the query filter of the resolved
/// context, so another workspace's draft is not found rather than found and refused.
/// </para>
/// <para>
/// <strong>Every body is scanned as it is written, whoever wrote it (LF.3.2).</strong> A generated body and a
/// creator's own are read by the same deterministic scanner against the recipe versions the revision is pinned
/// to, and what it finds is stored with the section. A finding never refuses a write: the text is saved exactly
/// as given, with the caution beside it.
/// </para>
/// </remarks>
internal sealed class LongFormSectionBusiness(
    ILongFormDraftDataLayer dataLayer,
    ILongFormClaimSourceReader pinnedRecipes,
    IAiClaimScanFacade claims,
    IWorkspaceContext workspace,
    IClock clock) : ILongFormSectionBusiness
{
    /// <summary>The most of a section's opening that is shown to the writer of a later one.</summary>
    internal const int FirstLineMaxCharacters = 240;

    public async Task<OperationResult<LongFormWritingContextServiceModel>> GetWritingContextAsync(
        Guid draftId, int revisionNumber, Guid sectionId, CancellationToken cancellationToken)
    {
        if (await dataLayer.FindAsync(draftId, cancellationToken) is not { } draft)
        {
            return Failure<LongFormWritingContextServiceModel>(NotFound());
        }

        if (RefuseUnlessWritable(draft) is { } refused)
        {
            return Failure<LongFormWritingContextServiceModel>(refused);
        }

        var latest = await dataLayer.FindLatestRevisionAsync(draftId, cancellationToken);

        if (latest is null || revisionNumber > latest.RevisionNumber)
        {
            return Failure<LongFormWritingContextServiceModel>(RevisionStale());
        }

        var document = LongFormDocument.Deserialize(latest.Content);
        var index = document.Sections.ToList().FindIndex(section => section.Id == sectionId);

        if (index < 0)
        {
            return Failure<LongFormWritingContextServiceModel>(SectionNotFound());
        }

        var target = document.Sections[index];

        // Settled says the creator is content with this text. Writing over it needs them to say otherwise first.
        if (target.Settled)
        {
            return Failure<LongFormWritingContextServiceModel>(Plain(
                ContentErrorCodes.LongFormSectionSettled,
                "This section is marked as settled. Clear the mark before it is written again."));
        }

        if (!await UnchangedSinceAsync(draftId, revisionNumber, latest, target, cancellationToken))
        {
            return Failure<LongFormWritingContextServiceModel>(RevisionStale());
        }

        var pins = await dataLayer.ListPinsAsync(latest.Id, cancellationToken);

        return OperationResult<LongFormWritingContextServiceModel>.Success(new(
            draft.WorkspaceId,
            draft.Id,
            draft.CreativeContextId,
            draft.Kind,

            // The revision the request named, not the latest: it is what the body will be landed against.
            revisionNumber,
            index,
            string.IsNullOrWhiteSpace(target.Body) ? null : target.Body,
            [
                .. document.Sections.Select(section => new LongFormWritingSectionServiceModel(
                    section.Id, section.Role, section.Heading, section.Intent, section.SourceReferenceId,
                    FirstLine(section.Body))),
            ],
            [
                .. pins.OrderBy(pin => pin.SortOrder).Select(pin => new LongFormWritingPinServiceModel(
                    pin.CreativeContextReferenceId, pin.RecipeId, pin.RecipeVersionId)),
            ],
            LongFormPolicy.TargetWords(draft.Kind),
            LongFormPolicy.SectionMaxCharacters));
    }

    public async Task<OperationResult<LongFormSectionsServiceModel>> GetSectionsAsync(
        Guid draftId, CancellationToken cancellationToken) =>
        await dataLayer.FindAsync(draftId, cancellationToken) is { } draft
            ? await DescribeAsync(draft, cancellationToken)
            : Failure<LongFormSectionsServiceModel>(NotFound());

    public async Task<OperationResult<LongFormSectionsServiceModel>> GetWritableSectionsAsync(
        Guid draftId, CancellationToken cancellationToken)
    {
        if (await dataLayer.FindAsync(draftId, cancellationToken) is not { } draft)
        {
            return Failure<LongFormSectionsServiceModel>(NotFound());
        }

        return RefuseUnlessWritable(draft) is { } refused
            ? Failure<LongFormSectionsServiceModel>(refused)
            : await DescribeAsync(draft, cancellationToken);
    }

    public async Task<OperationResult<LongFormSectionsServiceModel>> BeginWritingAsync(
        Guid draftId, CancellationToken cancellationToken)
    {
        var draft = await dataLayer.FindForUpdateAsync(draftId, cancellationToken);

        if (draft is null)
        {
            return Failure<LongFormSectionsServiceModel>(NotFound());
        }

        if (RefuseUnlessWritable(draft) is { } refused)
        {
            return Failure<LongFormSectionsServiceModel>(refused);
        }

        if (draft.Status is LongFormDraftStatus.Writing)
        {
            return await DescribeAsync(draft, cancellationToken);
        }

        var before = draft.Status;
        draft.Status = LongFormDraftStatus.Writing;
        draft.UpdatedAt = clock.UtcNow;

        // The plan closing is a transition of the draft, and is recorded as one. The actor is whoever the
        // resolved context says is asking; nothing about a section is in the entry.
        var audit = Audit(
            workspace.AccountId,
            ContentAuditActions.LongFormWritingBegan,
            draft,
            "Began writing the sections of a long-form draft, which closes its plan.",
            before);

        // Lost to another writer: whoever won has begun the writing too, or has moved the draft somewhere a
        // section may not be written. Read again and say which.
        if (await dataLayer.SaveAsync(draft, isNew: false, revision: null, [], audit, cancellationToken)
            is not LongFormDraftWrite.Saved)
        {
            return await dataLayer.FindAsync(draftId, cancellationToken) is { Status: LongFormDraftStatus.Writing } raced
                ? await DescribeAsync(raced, cancellationToken)
                : Failure<LongFormSectionsServiceModel>(DraftStale());
        }

        return await DescribeAsync(draft, cancellationToken);
    }

    public async Task<OperationResult<LongFormSectionsServiceModel>> RecordGeneratedSectionAsync(
        LongFormGeneratedSectionBodyInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        var draft = await dataLayer.FindForUpdateAsync(input.DraftId, cancellationToken);

        if (draft is null)
        {
            return Failure<LongFormSectionsServiceModel>(NotFound());
        }

        // Landing a proposal is idempotent, and this is where that holds: the worker lands a proposal as soon
        // as it commits and the maintenance sweep lands it again for a while afterwards.
        if (await dataLayer.HasRevisionForProposalAsync(input.DraftId, input.AiProposalId, cancellationToken))
        {
            return await DescribeAsync(draft, cancellationToken);
        }

        if (RefuseUnlessWritable(draft) is { } refused)
        {
            return Failure<LongFormSectionsServiceModel>(refused);
        }

        var latest = await dataLayer.FindLatestRevisionAsync(draft.Id, cancellationToken);

        if (latest is null || input.BasedOnRevisionNumber > latest.RevisionNumber || string.IsNullOrWhiteSpace(input.Body))
        {
            return Failure<LongFormSectionsServiceModel>(RevisionStale());
        }

        var document = LongFormDocument.Deserialize(latest.Content);

        if (document.Sections.FirstOrDefault(section => section.Id == input.SectionId) is not { } target)
        {
            return Failure<LongFormSectionsServiceModel>(SectionNotFound());
        }

        // The creator's text since the request was made outranks the answer to it. Neither is lost: the
        // proposal stays stored, and the section stays as the creator left it.
        if (target.Settled
            || !await UnchangedSinceAsync(draft.Id, input.BasedOnRevisionNumber, latest, target, cancellationToken))
        {
            return Failure<LongFormSectionsServiceModel>(Superseded());
        }

        var now = clock.UtcNow;
        var number = latest.RevisionNumber + 1;

        var revision = await NewRevisionAsync(
            draft,
            latest,
            ContentRevisionSource.AiGenerated,
            Replace(document, target with
            {
                Body = input.Body,
                Settled = false,
                Findings = await ScanAsync(target, input.Body, latest, cancellationToken),
                Provenance = new LongFormSectionProvenance(
                    ContentRevisionSource.AiGenerated,
                    input.AiProposalId,
                    input.PromptTemplateId,
                    input.PromptTemplateVersion,
                    input.PromptTemplateBodyChecksum,
                    ReusedEditorialRevisionId: null,
                    number,
                    input.ReusedEditorialParts is { Count: > 0 } parts ? [.. parts] : null),
            }),
            now,
            cancellationToken);

        revision.Revision.AiProposalId = input.AiProposalId;

        draft.Status = LongFormDraftStatus.Writing;
        draft.UpdatedAt = now;

        return await SaveAsync(draft, revision, audit: null, cancellationToken);
    }

    public async Task<OperationResult<LongFormSectionsServiceModel>> EditSectionAsync(
        string actorUserId, Guid draftId, Guid sectionId, EditLongFormSectionViewModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        var opened = await OpenAsync(draftId, sectionId, model.ExpectedConcurrencyToken, cancellationToken);

        if (opened.Refused is { } refused)
        {
            return Failure<LongFormSectionsServiceModel>(refused);
        }

        var (draft, latest, document, target) = opened.Found!.Value;

        // Blank is "not written": the section goes back to having no text, and nothing vouches for an empty one.
        var body = string.IsNullOrWhiteSpace(model.Body) ? null : model.Body;

        // Saving a section as it already reads is not an edit: no revision, no audit entry, no moved token.
        if (string.Equals(target.Body, body, StringComparison.Ordinal))
        {
            return await DescribeAsync(draft, cancellationToken);
        }

        var now = clock.UtcNow;
        var number = latest.RevisionNumber + 1;

        var revision = await NewRevisionAsync(
            draft,
            latest,
            ContentRevisionSource.CreatorEdit,
            Replace(document, target with
            {
                Body = body,

                // The creator's own words are read by the same scanner as anyone's. What it finds is shown to
                // them; it is never a reason their text is not saved.
                Findings = await ScanAsync(target, body, latest, cancellationToken),

                // Text that has changed is text nobody has yet called settled.
                Settled = false,
                Provenance = body is null
                    ? null
                    : new LongFormSectionProvenance(
                        ContentRevisionSource.CreatorEdit, null, null, null, null, ReusedEditorialRevisionId: null, number),
            }),
            now,
            cancellationToken);

        var before = draft.Status;
        draft.Status = LongFormDraftStatus.Writing;
        draft.UpdatedAt = now;

        return await SaveAsync(
            draft,
            revision,
            Audit(
                actorUserId,
                ContentAuditActions.LongFormSectionEdited,
                draft,
                $"Saved revision {Number(number)} of a long-form draft with one section edited.",
                before),
            cancellationToken);
    }

    public async Task<OperationResult<LongFormSectionsServiceModel>> SettleSectionAsync(
        string actorUserId, Guid draftId, Guid sectionId, SettleLongFormSectionViewModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        var opened = await OpenAsync(draftId, sectionId, model.ExpectedConcurrencyToken, cancellationToken);

        if (opened.Refused is { } refused)
        {
            return Failure<LongFormSectionsServiceModel>(refused);
        }

        var (draft, latest, document, target) = opened.Found!.Value;
        var settled = model.Settled!.Value;

        if (target.Settled == settled)
        {
            return await DescribeAsync(draft, cancellationToken);
        }

        // There is nothing to be settled about a section with no text.
        if (settled && string.IsNullOrWhiteSpace(target.Body))
        {
            return Failure<LongFormSectionsServiceModel>(Plain(
                ContentErrorCodes.LongFormSectionNotWritten,
                "This section has not been written yet, so it cannot be marked as settled."));
        }

        var now = clock.UtcNow;

        // The body and where it came from are carried over untouched: settling says the creator is content
        // with the text, and changes none of it. It is not acceptance, which is of the whole document.
        var revision = await NewRevisionAsync(
            draft, latest, ContentRevisionSource.CreatorEdit, Replace(document, target with { Settled = settled }), now,
            cancellationToken);

        var before = draft.Status;
        draft.Status = LongFormDraftStatus.Writing;
        draft.UpdatedAt = now;

        return await SaveAsync(
            draft,
            revision,
            Audit(
                actorUserId,
                settled ? ContentAuditActions.LongFormSectionSettled : ContentAuditActions.LongFormSectionUnsettled,
                draft,
                $"Saved revision {Number(revision.Revision.RevisionNumber)} of a long-form draft with one section "
                    + (settled ? "marked settled." : "no longer marked settled."),
                before),
            cancellationToken);
    }

    /// <summary>
    /// How a written section begins: its first line that says anything, as the creator has it.
    /// </summary>
    /// <remarks>
    /// Cut at a fixed length rather than at a sentence, so nothing here has to decide where a sentence ends.
    /// </remarks>
    internal static string? FirstLine(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        var line = body.Split('\n').Select(candidate => candidate.Trim()).First(candidate => candidate.Length > 0);

        return line.Length <= FirstLineMaxCharacters ? line : line[..FirstLineMaxCharacters];
    }

    /// <summary>
    /// What the claim scanner finds in a body, against the recipe versions the revision is pinned to.
    /// </summary>
    /// <remarks>
    /// The sources are the recipe versions the revision holds pins to: the recipe as it stood when the piece
    /// was planned, not as it stands now.
    /// </remarks>
    private async Task<IReadOnlyList<LongFormSectionFinding>?> ScanAsync(
        LongFormSection section, string? body, LongFormRevision latest, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        var pinned = await pinnedRecipes.ReadAsync(
            await dataLayer.ListPinsAsync(latest.Id, cancellationToken), cancellationToken);

        var found = LongFormClaimScanner.Scan(claims, section.Role, section.SourceReferenceId, body, pinned);

        return found.Count == 0 ? null : found;
    }

    /// <summary>The draft, its latest revision and one section of it, for a creator's own write.</summary>
    private async Task<(OperationError? Refused, (LongFormDraft, LongFormRevision, LongFormDocument, LongFormSection)? Found)> OpenAsync(
        Guid draftId, Guid sectionId, string? expectedToken, CancellationToken cancellationToken)
    {
        var draft = await dataLayer.FindForUpdateAsync(draftId, cancellationToken);

        if (draft is null)
        {
            return (NotFound(), null);
        }

        if (!CreativeContextConcurrencyToken.Matches(expectedToken, draft.RowVersion))
        {
            return (DraftStale(), null);
        }

        if (RefuseUnlessWritable(draft) is { } refused)
        {
            return (refused, null);
        }

        var latest = await dataLayer.FindLatestRevisionAsync(draft.Id, cancellationToken);

        if (latest is null)
        {
            return (DraftStale(), null);
        }

        var document = LongFormDocument.Deserialize(latest.Content);

        return document.Sections.FirstOrDefault(section => section.Id == sectionId) is { } target
            ? (null, (draft, latest, document, target))
            : (SectionNotFound(), null);
    }

    /// <summary>
    /// Whether one section reads now as it did at the revision a request named.
    /// </summary>
    /// <remarks>
    /// Compared as text, letter for letter. A revision that cannot be found is not one the request could have
    /// been made against.
    /// </remarks>
    private async Task<bool> UnchangedSinceAsync(
        Guid draftId, int revisionNumber, LongFormRevision latest, LongFormSection target, CancellationToken cancellationToken)
    {
        if (revisionNumber == latest.RevisionNumber)
        {
            return true;
        }

        if (await dataLayer.FindRevisionAsync(draftId, revisionNumber, cancellationToken) is not { } then)
        {
            return false;
        }

        var before = LongFormDocument.Deserialize(then.Content).Sections.FirstOrDefault(section => section.Id == target.Id);

        return before is not null && string.Equals(before.Body, target.Body, StringComparison.Ordinal);
    }

    /// <summary>The one refusal for a draft whose sections may not be written or changed now.</summary>
    internal static OperationError? RefuseUnlessWritable(LongFormDraft draft) => draft.Status switch
    {
        LongFormDraftStatus.Outlined or LongFormDraftStatus.Writing => null,
        LongFormDraftStatus.Draft => Plain(
            ContentErrorCodes.LongFormPlanNotApproved,
            "This draft's plan has not been approved yet. Approve the plan before any section is written."),
        LongFormDraftStatus.Accepted => Plain(
            ContentErrorCodes.LongFormDraftAccepted,
            "This draft has been accepted, so its sections are no longer being written."),
        _ => Plain(
            ContentErrorCodes.LongFormDraftNotWritable,
            "This draft's sections are not being written at the moment."),
    };

    private static LongFormDocument Replace(LongFormDocument document, LongFormSection replacement) =>
        new([.. document.Sections.Select(section => section.Id == replacement.Id ? replacement : section)]);

    /// <summary>
    /// The next revision, written against what its parent was written against: the sources did not change
    /// because one section's text did. Its pins are carried forward whole.
    /// </summary>
    private async Task<(LongFormRevision Revision, List<LongFormRevisionRecipePin> Pins)> NewRevisionAsync(
        LongFormDraft draft,
        LongFormRevision latest,
        ContentRevisionSource source,
        LongFormDocument document,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var revision = new LongFormRevision
        {
            Id = Guid.NewGuid(),
            LongFormDraftId = draft.Id,
            RevisionNumber = latest.RevisionNumber + 1,
            ParentRevisionId = latest.Id,
            Source = source,
            SchemaVersion = LongFormDocument.CurrentSchemaVersion,
            Content = document.Serialize(),
            BrandProfileRevisionId = latest.BrandProfileRevisionId,
            BrandStyleGuideVersionId = latest.BrandStyleGuideVersionId,
            PromptTemplateId = latest.PromptTemplateId,
            PromptTemplateVersion = latest.PromptTemplateVersion,
            PromptTemplateBodyChecksum = latest.PromptTemplateBodyChecksum,
            CreativeContextVersion = latest.CreativeContextVersion,
            CreatedByMembershipId = workspace.MembershipId,
            CreatedAt = now,
        };

        var pins = (await dataLayer.ListPinsAsync(latest.Id, cancellationToken))
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

        return (revision, pins);
    }

    private async Task<OperationResult<LongFormSectionsServiceModel>> SaveAsync(
        LongFormDraft draft,
        (LongFormRevision Revision, List<LongFormRevisionRecipePin> Pins) revision,
        AuditEntry? audit,
        CancellationToken cancellationToken) =>
        await dataLayer.SaveAsync(draft, isNew: false, revision.Revision, revision.Pins, audit, cancellationToken) switch
        {
            LongFormDraftWrite.Saved => await DescribeAsync(draft, cancellationToken),

            // Stale and refused alike: another write to this draft landed first — the token moved, or the next
            // revision number was taken. The caller's answer is the same, and so is its remedy.
            _ => Failure<LongFormSectionsServiceModel>(DraftStale()),
        };

    /// <summary>The draft's sections as they stand, read after any write so the token is the one now in force.</summary>
    private async Task<OperationResult<LongFormSectionsServiceModel>> DescribeAsync(
        LongFormDraft draft, CancellationToken cancellationToken)
    {
        var latest = await dataLayer.FindLatestRevisionAsync(draft.Id, cancellationToken);
        var document = latest is null ? new LongFormDocument([]) : LongFormDocument.Deserialize(latest.Content);

        return OperationResult<LongFormSectionsServiceModel>.Success(new(
            draft.Id,
            draft.CreativeContextId,
            draft.Kind,
            draft.Status,
            latest?.RevisionNumber,
            CreativeContextConcurrencyToken.From(draft.RowVersion),
            LongFormPolicy.SectionMaxCharacters,
            [
                .. document.Sections.Select(section =>
                {
                    var written = !string.IsNullOrWhiteSpace(section.Body);
                    var count = written ? LongFormWordCounter.CountCharacters(section.Body) : 0;

                    return new LongFormSectionStateServiceModel(
                        section.Id,
                        section.Role,
                        section.Heading,
                        section.Intent,
                        written ? section.Body : null,
                        written,
                        section.Settled,
                        count,
                        count > LongFormPolicy.SectionMaxCharacters,
                        written ? section.Provenance?.Source : null,
                        written ? section.Provenance?.AiProposalId : null);
                }),
            ]));
    }

    // State references only: a revision number and statuses — never a word of a section.
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
        var traceId = System.Diagnostics.Activity.Current?.TraceId;

        return traceId is { } id && id != default ? Guid.ParseExact(id.ToHexString(), "N") : Guid.NewGuid();
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static OperationResult<T> Failure<T>(OperationError error) => OperationResult<T>.Failure(error);

    private static OperationError Plain(string code, string message) =>
        new(code, message, new Dictionary<string, string[]>());

    private static OperationError NotFound() =>
        Plain(ContentErrorCodes.LongFormDraftNotFound, "That long-form draft does not exist.");

    private static OperationError DraftStale() => Plain(
        ContentErrorCodes.LongFormDraftStale,
        "This draft has changed since you opened it. Reload it and make your change again.");

    private static OperationError RevisionStale() => Plain(
        ContentErrorCodes.LongFormRevisionStale,
        "This section has changed since it was asked for. Reload the draft and ask again.");

    private static OperationError SectionNotFound() =>
        Plain(ContentErrorCodes.LongFormSectionNotFound, "That section is not part of this draft's plan.");

    private static OperationError Superseded() => Plain(
        ContentErrorCodes.LongFormSectionSuperseded,
        "This section was changed or settled after it was asked for, so what was written was not put in its place.");
}

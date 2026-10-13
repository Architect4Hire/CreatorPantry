using System.Globalization;
using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Content.Data;
using CreatorPantry.Domain.Modules.Content.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Managers;

namespace CreatorPantry.Domain.Modules.Content.Business;

/// <summary>A long-form draft read whole, and its acceptance (LF.3.4).</summary>
public interface ILongFormDocumentBusiness
{
    /// <summary>The draft's latest revision assembled into one document.</summary>
    Task<OperationResult<LongFormAssembledDocumentServiceModel>> GetDocumentAsync(Guid draftId, CancellationToken cancellationToken);

    /// <summary>Accepts the draft's latest revision as the article of record.</summary>
    Task<OperationResult<LongFormAssembledDocumentServiceModel>> AcceptAsync(
        string actorUserId, Guid draftId, AcceptLongFormDocumentViewModel model, CancellationToken cancellationToken);
}

/// <inheritdoc />
/// <remarks>
/// <para>
/// <strong>Acceptance is of the document.</strong> It points the draft at one revision and says when. It
/// writes no revision of its own, so there is nothing for a second acceptance to duplicate: accepting what is
/// already accepted returns it, and records nothing.
/// </para>
/// <para>
/// <strong>What is wrong must be acknowledged, by name.</strong> A document with an unwritten section, or with
/// text over a ceiling, can be accepted — it is the creator's article and the creator's call — but only by a
/// request that lists each issue the document has. One that lists fewer is told what it left out and accepts
/// nothing. What counts as an issue is decided here, from the revision, and never by the request.
/// </para>
/// <para>
/// <strong>A document with nothing written is not one.</strong> It cannot be accepted whatever is confirmed.
/// </para>
/// <para>
/// <strong>There is no stored "assembled" state.</strong> A draft goes from being written to accepted. What
/// "assembled" names is the projection read here, which exists for any revision at any time.
/// </para>
/// </remarks>
internal sealed class LongFormDocumentBusiness(ILongFormDraftDataLayer dataLayer, IClock clock) : ILongFormDocumentBusiness
{
    public async Task<OperationResult<LongFormAssembledDocumentServiceModel>> GetDocumentAsync(
        Guid draftId, CancellationToken cancellationToken) =>
        await dataLayer.FindAsync(draftId, cancellationToken) is { } draft
            ? Success(await DescribeAsync(draft, cancellationToken))
            : Failure(NotFound());

    public async Task<OperationResult<LongFormAssembledDocumentServiceModel>> AcceptAsync(
        string actorUserId, Guid draftId, AcceptLongFormDocumentViewModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        var draft = await dataLayer.FindForUpdateAsync(draftId, cancellationToken);

        if (draft is null)
        {
            return Failure(NotFound());
        }

        var latest = await dataLayer.FindLatestRevisionAsync(draft.Id, cancellationToken);

        // Accepting what is already accepted changes nothing and is not an error — checked before the token,
        // because the first acceptance moved it, and a retry after a lost response still holds the old one.
        if (draft.Status is LongFormDraftStatus.Accepted
            && latest is not null
            && draft.AcceptedRevisionId == latest.Id
            && latest.RevisionNumber == model.RevisionNumber)
        {
            return Success(await DescribeAsync(draft, cancellationToken));
        }

        if (!CreativeContextConcurrencyToken.Matches(model.ExpectedConcurrencyToken, draft.RowVersion))
        {
            return Failure(Stale());
        }

        if (LongFormSectionBusiness.RefuseUnlessWritable(draft) is { } refused)
        {
            return Failure(refused);
        }

        // The acceptance names what was read. A document changed since is a different document.
        if (latest is null || latest.RevisionNumber != model.RevisionNumber)
        {
            return Failure(Stale());
        }

        var assembled = LongFormDocumentAssembler.Assemble(draft.Kind, LongFormDocument.Deserialize(latest.Content));

        if (assembled.Sections.All(section => !section.IsWritten))
        {
            return Failure(Plain(
                ContentErrorCodes.LongFormAcceptanceNothingWritten,
                "None of this piece has been written yet, so there is nothing to accept."));
        }

        var issues = Issues(assembled);
        var confirmed = (model.Confirmed ?? []).ToHashSet();
        var unconfirmed = issues.Where(issue => !confirmed.Contains(issue.Issue)).ToList();

        // Exactly what is wrong, no less and no more. A request that confirms an issue the document does not
        // have was not composed against this document — or confirms everything as a habit, which is the one
        // thing a confirmation by name exists to prevent.
        if (unconfirmed.Count > 0 || confirmed.Count != issues.Count)
        {
            if (unconfirmed.Count == 0)
            {
                unconfirmed = issues;
            }

            return Failure(new OperationError(
                ContentErrorCodes.LongFormAcceptanceConfirmationRequired,
                "This piece is not finished. Confirm what is wrong with it to accept it as it is.",
                new Dictionary<string, string[]>
                {
                    ["confirmed"] = unconfirmed.Count > 0
                        ? [.. unconfirmed.Select(issue => Explain(issue))]
                        : ["Nothing is wrong with this piece, so there is nothing to confirm."],
                },
                new Dictionary<string, object?>
                {
                    // Every issue the document has, not only the ones left out: the client confirms the lot.
                    ["issues"] = issues.Select(issue => new
                    {
                        issue = issue.Issue.ToString(),
                        sectionIds = issue.SectionIds,
                    }).ToList(),
                }));
        }

        var before = draft.Status;
        var now = clock.UtcNow;

        draft.AcceptedRevisionId = latest.Id;
        draft.AcceptedAt = now;
        draft.Status = LongFormDraftStatus.Accepted;
        draft.UpdatedAt = now;

        var audit = new AuditEntry(
            actorUserId,
            ContentAuditActions.LongFormDocumentAccepted,
            ContentAuditActions.LongFormDraftResourceType,
            draft.Id.ToString("D"),
            CorrelationId(),

            // Counts and a revision number only — never a heading or a word of the piece.
            $"Accepted revision {Number(latest.RevisionNumber)} of a long-form draft"
                + (issues.Count == 0 ? "." : $", confirming {Number(issues.Count)} thing(s) wrong with it."),
            BeforeReference: before.ToString(),
            AfterReference: draft.Status.ToString());

        return await dataLayer.SaveAsync(draft, isNew: false, revision: null, [], audit, cancellationToken) switch
        {
            LongFormDraftWrite.Saved => Success(await DescribeAsync(draft, cancellationToken)),
            _ => Failure(Stale()),
        };
    }

    /// <summary>What is wrong with a document, in a fixed order, each with the sections it is wrong about.</summary>
    internal static List<LongFormAcceptanceIssueDetailServiceModel> Issues(LongFormAssembledDocument assembled)
    {
        var issues = new List<LongFormAcceptanceIssueDetailServiceModel>(2);
        var unwritten = assembled.Sections.Where(section => !section.IsWritten).Select(section => section.Id).ToList();
        var over = assembled.Sections.Where(section => section.OverCeiling).Select(section => section.Id).ToList();

        if (unwritten.Count > 0)
        {
            issues.Add(new(LongFormAcceptanceIssue.UnwrittenSections, unwritten));
        }

        // Sections over their own ceiling are named; a document over its ceiling with no one section to blame
        // is the same issue with no section named.
        if (over.Count > 0 || assembled.DocumentOverCeiling)
        {
            issues.Add(new(LongFormAcceptanceIssue.OverCeiling, over));
        }

        return issues;
    }

    private static string Explain(LongFormAcceptanceIssueDetailServiceModel issue) => issue.Issue switch
    {
        LongFormAcceptanceIssue.UnwrittenSections =>
            $"{Number(issue.SectionIds.Count)} section(s) of the plan have not been written.",
        _ => issue.SectionIds.Count > 0
            ? $"{Number(issue.SectionIds.Count)} section(s) are longer than a section may be."
            : "The piece as a whole is longer than it may be.",
    };

    private async Task<LongFormAssembledDocumentServiceModel> DescribeAsync(LongFormDraft draft, CancellationToken cancellationToken)
    {
        var latest = await dataLayer.FindLatestRevisionAsync(draft.Id, cancellationToken);
        var document = latest is null ? new LongFormDocument([]) : LongFormDocument.Deserialize(latest.Content);
        var planned = document.Sections.ToDictionary(section => section.Id);
        var assembled = LongFormDocumentAssembler.Assemble(draft.Kind, document);

        var sources = latest is null
            ? await dataLayer.ReadItemSourcesAsync(draft, document, [], cancellationToken)
            : await dataLayer.ReadItemSourcesAsync(
                draft, document, await dataLayer.ListPinsAsync(latest.Id, cancellationToken), cancellationToken);

        // The accepted revision is the latest one for as long as the draft is accepted: nothing is written
        // to an accepted draft. Read by id all the same, so this says what is stored rather than what is assumed.
        int? acceptedNumber = draft.AcceptedRevisionId is { } acceptedId
            ? latest?.Id == acceptedId ? latest.RevisionNumber : null
            : null;

        return new LongFormAssembledDocumentServiceModel(
            draft.Id,
            draft.CreativeContextId,
            draft.Kind,
            draft.Status,
            latest?.RevisionNumber,
            CreativeContextConcurrencyToken.From(draft.RowVersion),
            assembled.Markdown,
            assembled.WordCount,
            assembled.CharacterCount,
            assembled.TargetWords,
            assembled.WordTarget,
            LongFormPolicy.SectionMaxCharacters,
            LongFormPolicy.DocumentMaxCharacters,
            assembled.DocumentOverCeiling,
            [
                .. assembled.Sections.Select(section => new LongFormAssembledSectionServiceModel(
                    section.Id,
                    section.Role,
                    section.Heading,
                    planned[section.Id].Intent,
                    section.Body,
                    section.IsWritten,
                    section.Settled,
                    section.CharacterCount,
                    section.OverCeiling,
                    section.Provenance?.Source,
                    section.Provenance?.AiProposalId,
                    section.Provenance?.ReusedEditorialParts ?? [],
                    section.Findings,
                    planned[section.Id].SourceReferenceId,
                    sources.TryGetValue(section.Id, out var state) ? state : null)),
            ],
            Issues(assembled),
            acceptedNumber,
            draft.AcceptedAt,
            document.Framing is { } framing ? new LongFormFramingServiceModel(framing.Subject, framing.Note) : null,
            sources.Unresolved(document),
            Reasons(draft.StaleReasons),
            draft.StaleSince);
    }

    /// <summary>The reasons recorded against a draft, one to an entry, in the order the flags are declared.</summary>
    private static List<ContentStaleReasons> Reasons(ContentStaleReasons recorded) =>
    [
        .. Enum.GetValues<ContentStaleReasons>().Where(reason => reason is not ContentStaleReasons.None && recorded.HasFlag(reason)),
    ];

    private static Guid CorrelationId()
    {
        var traceId = System.Diagnostics.Activity.Current?.TraceId;

        return traceId is { } id && id != default ? Guid.ParseExact(id.ToHexString(), "N") : Guid.NewGuid();
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static OperationResult<LongFormAssembledDocumentServiceModel> Success(LongFormAssembledDocumentServiceModel model) =>
        OperationResult<LongFormAssembledDocumentServiceModel>.Success(model);

    private static OperationResult<LongFormAssembledDocumentServiceModel> Failure(OperationError error) =>
        OperationResult<LongFormAssembledDocumentServiceModel>.Failure(error);

    private static OperationError Plain(string code, string message) =>
        new(code, message, new Dictionary<string, string[]>());

    private static OperationError NotFound() =>
        Plain(ContentErrorCodes.LongFormDraftNotFound, "That long-form draft does not exist.");

    private static OperationError Stale() => Plain(
        ContentErrorCodes.LongFormDraftStale,
        "This draft has changed since you opened it. Reload it and make your change again.");
}

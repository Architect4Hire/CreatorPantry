using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Brand.Data;
using CreatorPantry.Domain.Modules.Brand.Data.Entities;
using CreatorPantry.Domain.Modules.Brand.Managers;

namespace CreatorPantry.Domain.Modules.Brand.Business;

public interface IBrandStyleGuideBusiness
{
    /// <summary>Reads one guide with its working and active versions. No model is called, nothing is written.</summary>
    Task<OperationResult<BrandStyleGuideDetailServiceModel>> GetAsync(Guid guideId, CancellationToken cancellationToken);

    /// <summary>
    /// One page of a guide's version history, newest version first: metadata only, and nothing is written —
    /// listing a version does not approve it, activate it or change its status.
    /// </summary>
    /// <returns>
    /// The page, or <c>brand.guide.not_found</c> when the resolved workspace has no such guide. An existing
    /// guide with no citations and no approvals still answers with its versions; an empty page is never how a
    /// missing guide is reported.
    /// </returns>
    Task<OperationResult<CursorPageServiceModel<BrandStyleGuideVersionSummaryServiceModel>>> ListVersionsAsync(
        BrandStyleGuideVersionListCriteria criteria, CancellationToken cancellationToken);

    /// <summary>
    /// Compares two of one guide's versions. Deterministic, computed by <see cref="BrandStyleGuideComparer"/>,
    /// and writes nothing — no approval, no activation, and no record that the comparison happened.
    /// </summary>
    /// <returns>
    /// The comparison, or <c>brand.guide.not_found</c> for a guide this workspace cannot read, or
    /// <c>brand.guide.version.not_found</c> naming whichever of <c>from</c> and <c>to</c> the guide does not
    /// have — both of them when both are wrong.
    /// </returns>
    Task<OperationResult<BrandStyleGuideVersionComparisonServiceModel>> CompareVersionsAsync(
        Guid guideId, int fromVersionNumber, int toVersionNumber, CancellationToken cancellationToken);

    /// <summary>
    /// Creates a guide and its version 1 from an already-validated, blank-free draft. No model is called.
    /// </summary>
    Task<OperationResult<BrandStyleGuideServiceModel>> CreateAsync(
        string userId, BrandStyleGuideDraft draft, CancellationToken cancellationToken);

    /// <summary>
    /// Applies a creator's own edit to the guide's working version and writes the result as one further
    /// immutable version (11A.15). No model is called, and nothing is approved or activated.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The working-version check is the whole guard, and it belongs here.</strong> The caller read the
    /// guide before the transaction opened; this reads it inside, so an edit that landed in between is refused
    /// rather than applied to words the creator never saw. Both numbers are stated in the refusal — they are
    /// version numbers of the caller's own guide, not secrets, and a creator told only "it moved" cannot tell
    /// whether they are one edit or ten behind.
    /// </para>
    /// <para>
    /// <strong>Nothing differing writes nothing.</strong> A save whose result matches the working version word
    /// for word produces no version and no audit entry, and reports the version that still stands — the same
    /// no-op rule accepted AI guidance follows.
    /// </para>
    /// <para>
    /// <strong>The guide row is deliberately left alone</strong>, as on the proposal path: a version is the
    /// guide's content, while the row carries its name, purpose and archived state. Touching <c>UpdatedAt</c>
    /// would bump the row version and invalidate a concurrency token a client is holding for fields this change
    /// did not alter.
    /// </para>
    /// </remarks>
    Task<OperationResult<BrandStyleGuideVersionSavedServiceModel>> SaveVersionAsync(
        string userId, Guid guideId, BrandStyleGuideEditDraft edit, CancellationToken cancellationToken);

    /// <summary>
    /// Approves one version of one guide: marks it finished, which is what makes it activatable. Nothing in
    /// the guide is edited, and nothing is activated.
    /// </summary>
    /// <returns>
    /// The approval, or the first refusal that applies: <c>brand.guide.not_found</c>,
    /// <c>brand.guide.version.not_found</c>, <c>brand.guide.archived.conflict</c>,
    /// <c>brand.guide.version.empty.conflict</c>, or <c>brand.guide.version.approval.conflict</c>.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong>Stale citations are not a refusal here</strong>, although they are one for activation. Approval
    /// says the wording is finished, which stays true when a cited document is replaced afterwards — and since
    /// a document can be replaced at any time after an approval, refusing on it here would not remove the case
    /// activation has to handle anyway. One rule, in the one place it decides something.
    /// </para>
    /// <para>
    /// An already-approved version is a success that writes nothing, reporting the original approver, time and
    /// words rather than this request's.
    /// </para>
    /// </remarks>
    Task<OperationResult<BrandStyleGuideApprovalResultServiceModel>> ApproveVersionAsync(
        string userId,
        Guid guideId,
        int versionNumber,
        string? reason,
        CancellationToken cancellationToken);

    /// <summary>
    /// Makes one approved version of one guide the workspace's default. Nothing in the guide is edited: the
    /// version is immutable and this writes only the workspace's one activation decision.
    /// </summary>
    /// <param name="expectedActiveVersionId">
    /// The version the caller believes currently holds the default, or null for "this workspace has none".
    /// Checked before anything is written.
    /// </param>
    /// <returns>
    /// The activation, or the first refusal that applies: <c>brand.guide.not_found</c>,
    /// <c>brand.guide.version.not_found</c>, <c>brand.guide.archived.conflict</c>,
    /// <c>brand.guide.version.unapproved.conflict</c>, <c>brand.guide.version.stale.conflict</c>,
    /// <c>brand.guide.version.empty.conflict</c>, or <c>brand.guide.activation.conflict</c>.
    /// </returns>
    /// <remarks>
    /// Eligibility is settled before the expectation. A version that is a draft, stale or empty is ineligible
    /// however fresh the caller's picture of the workspace is, and that refusal will not read differently
    /// after a re-read — so it is the more useful one to give first. The expectation is about the world having
    /// moved, and is checked last, immediately before the write.
    /// </remarks>
    Task<OperationResult<BrandStyleGuideActivationResultServiceModel>> ActivateVersionAsync(
        string userId,
        Guid guideId,
        int versionNumber,
        Guid? expectedActiveVersionId,
        string? reason,
        CancellationToken cancellationToken);

    /// <summary>
    /// Writes one further draft version of a guide from guidance a creator accepted out of an AI proposal
    /// (11A.18), laid over the guide's working version.
    /// </summary>
    /// <returns>
    /// The version written — or the fact that none was, when the accepted guidance already matched — or the
    /// first refusal that applies: <c>brand.guide.not_found</c>, <c>brand.guide.archived.conflict</c>,
    /// <c>brand.guide.workingVersion.conflict</c>, <c>brand.guide.invalid_request</c>,
    /// <c>brand.guide.source.unprocessable</c>, or <c>brand.guide.version.limit.invalid_request</c>.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong>Everything a creator-typed version obeys, this obeys.</strong> Section and rule lengths, the
    /// rule cap, the channel-variant cap, the source cap, and the requirement that every cited pointer resolve
    /// to an exact version this workspace owns — all checked here, because they are this module's invariants and
    /// a model's output may not reach a guide by a shorter route.
    /// </para>
    /// <para>
    /// <strong>It approves nothing and activates nothing.</strong> No <c>BrandStyleGuideApproval</c> is written
    /// and the workspace default is not touched, so the version is a draft like any other and becoming the
    /// default still needs an approval and an Owner.
    /// </para>
    /// <para>
    /// <strong>The guide row is deliberately left alone.</strong> A version is the guide's content; the guide row
    /// carries its name, purpose and archived state, none of which this writes. Touching <c>UpdatedAt</c> would
    /// bump the row version and invalidate a concurrency token a client is holding for fields this change did
    /// not alter.
    /// </para>
    /// </remarks>
    Task<OperationResult<BrandStyleGuideVersionCreatedServiceModel>> CreateVersionFromProposalAsync(
        string userId,
        Guid guideId,
        BrandStyleGuideProposalApplication application,
        CancellationToken cancellationToken);

    /// <inheritdoc cref="Facade.IBrandStyleGuideFacade.GetActiveAsync"/>
    Task<OperationResult<BrandActiveStyleGuideServiceModel?>> GetActiveAsync(CancellationToken cancellationToken);
}

/// <inheritdoc cref="IBrandStyleGuideBusiness"/>
internal sealed class BrandStyleGuideBusiness(
    IBrandStyleGuideDataLayer dataLayer,
    IWorkspaceContext workspace,
    IClock clock) : IBrandStyleGuideBusiness
{
    public async Task<OperationResult<BrandStyleGuideDetailServiceModel>> GetAsync(
        Guid guideId, CancellationToken cancellationToken)
    {
        if (await dataLayer.ReadAsync(guideId, cancellationToken) is not { } read)
        {
            return OperationResult<BrandStyleGuideDetailServiceModel>.Failure(new OperationError(
                BrandErrorCodes.GuideNotFound,
                "There is no such brand style guide.",
                new Dictionary<string, string[]>()));
        }

        var guide = read.Guide;

        return OperationResult<BrandStyleGuideDetailServiceModel>.Success(new BrandStyleGuideDetailServiceModel(
            guide.Id,
            guide.DisplayName,
            guide.Purpose,
            guide.Status,
            guide.ArchivedAt,
            guide.CreatedAt,
            guide.UpdatedAt,
            BrandConcurrencyToken.From(guide.RowVersion),
            Map(read.Working, activation: null),
            read.Active is null
                ? null
                : Map(read.Active, new BrandStyleGuideActivationServiceModel(read.ActivatedAt!.Value, read.ActivationReason))));
    }

    public async Task<OperationResult<BrandActiveStyleGuideServiceModel?>> GetActiveAsync(
        CancellationToken cancellationToken)
    {
        // Null in success, not a failure: a workspace that has activated nothing has no active guide, and that
        // is an answer a caller acts on rather than an error it recovers from.
        if (await dataLayer.ReadActiveAsync(cancellationToken) is not { } read)
        {
            return OperationResult<BrandActiveStyleGuideServiceModel?>.Success(null);
        }

        return OperationResult<BrandActiveStyleGuideServiceModel?>.Success(new BrandActiveStyleGuideServiceModel(
            read.Guide.Id,
            read.Guide.DisplayName,
            read.Guide.Purpose,
            read.Guide.Status,
            Map(read.Version, new BrandStyleGuideActivationServiceModel(read.ActivatedAt, read.ActivationReason)),
            read.StaleSourceCount));
    }

    public async Task<OperationResult<CursorPageServiceModel<BrandStyleGuideVersionSummaryServiceModel>>> ListVersionsAsync(
        BrandStyleGuideVersionListCriteria criteria, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(criteria);

        // Asked first, so an unknown guide and another workspace's are one answer rather than an empty page.
        // A guide always has at least version 1, so "no rows" could only ever mean a guide that is not there.
        if (!await dataLayer.GuideExistsAsync(criteria.GuideId, cancellationToken))
        {
            return OperationResult<CursorPageServiceModel<BrandStyleGuideVersionSummaryServiceModel>>.Failure(new OperationError(
                BrandErrorCodes.GuideNotFound,
                "There is no such brand style guide.",
                new Dictionary<string, string[]>()));
        }

        var page = await dataLayer.ListVersionsAsync(criteria, cancellationToken);

        // The cursor is minted here, where the scope is known: a repository is handed a predicate, not a route.
        return OperationResult<CursorPageServiceModel<BrandStyleGuideVersionSummaryServiceModel>>.Success(
            PageBuilder.Build(page.Rows, page.HasMore, criteria.Scope, row => new BrandStyleGuideVersionSummaryServiceModel(
                row.Id,
                row.VersionNumber,
                row.IsApproved ? BrandStyleGuideVersionStatus.Approved : BrandStyleGuideVersionStatus.Draft,
                row.SourceCount,
                page.StaleSourceCounts.TryGetValue(row.Id, out var stale) ? stale : 0,
                row.CreatedByMembershipId,
                row.ChangeReason,
                row.CreatedAt,
                row.IsActive)));
    }

    public async Task<OperationResult<BrandStyleGuideVersionComparisonServiceModel>> CompareVersionsAsync(
        Guid guideId, int fromVersionNumber, int toVersionNumber, CancellationToken cancellationToken)
    {
        if (await dataLayer.ReadForComparisonAsync(guideId, fromVersionNumber, toVersionNumber, cancellationToken)
            is not { } read)
        {
            // The guide is not visible. Answered identically to one that was never created (tenancy.md).
            return OperationResult<BrandStyleGuideVersionComparisonServiceModel>.Failure(new OperationError(
                BrandErrorCodes.GuideNotFound,
                "There is no such brand style guide.",
                new Dictionary<string, string[]>()));
        }

        // Both sides are named, so a creator who mistyped one number is told which one. Reporting only the
        // first would send them round the loop twice when they mistyped both.
        var missing = new List<(string Field, string Error)>();

        if (read.From is null)
        {
            missing.Add(("from", $"This guide has no version {fromVersionNumber}."));
        }

        // Equal numbers are one question, so a single "no such version" rather than the same one twice.
        if (read.To is null && toVersionNumber != fromVersionNumber)
        {
            missing.Add(("to", $"This guide has no version {toVersionNumber}."));
        }

        if (missing.Count > 0)
        {
            return OperationResult<BrandStyleGuideVersionComparisonServiceModel>.Failure(OperationError.Validation(
                BrandErrorCodes.GuideVersionNotFound, "Those versions could not be compared.", missing));
        }

        var from = read.From!;

        // Equal numbers read the one version as both sides rather than as a missing one: comparing a version
        // with itself is a legitimate question whose answer is "nothing changed".
        var to = read.To ?? from;

        return OperationResult<BrandStyleGuideVersionComparisonServiceModel>.Success(
            new BrandStyleGuideVersionComparisonServiceModel
            {
                From = Side(from),
                To = Side(to),
                Comparison = BrandStyleGuideComparer.Compare(Input(from), Input(to)),
            });
    }

    private static BrandStyleGuideComparisonSideServiceModel Side(BrandStyleGuideComparisonVersionRead read) => new(
        read.Version.Id,
        read.Version.VersionNumber,
        read.IsApproved ? BrandStyleGuideVersionStatus.Approved : BrandStyleGuideVersionStatus.Draft,
        read.Version.CreatedAt);

    /// <summary>
    /// Turns one stored version into the values the comparer works on.
    /// </summary>
    /// <remarks>
    /// The empty channel key becomes null here, exactly as the single-guide read does, so the two routes
    /// describe the same section the same way. Translation between stored and application shapes is
    /// Business's job; the comparer never sees an entity.
    /// </remarks>
    private static BrandStyleGuideComparisonInput Input(BrandStyleGuideComparisonVersionRead read) => new(
        [
            .. read.Version.Sections.Select(section => new BrandStyleGuideSectionServiceModel(
                section.SectionKey,
                section.ChannelKey.Length == 0 ? null : section.ChannelKey,
                section.Body)),
        ],
        [
            .. read.Version.Rules.Select(rule => new BrandStyleGuideComparisonRuleInput(
                rule.Kind, rule.Text, rule.SortOrder)),
        ],
        [
            .. read.Citations.Select(citation => new BrandStyleGuideSourceServiceModel(
                citation.DocumentId, citation.VersionNumber)),
        ]);

    private static BrandStyleGuideVersionDetailServiceModel Map(
        BrandStyleGuideVersionRead read, BrandStyleGuideActivationServiceModel? activation) => new(
        read.Version.Id,
        read.Version.VersionNumber,
        read.ParentVersionNumber,
        read.Version.ChangeReason,
        read.Version.CreatedAt,
        read.Approval is null ? null : new BrandStyleGuideApprovalServiceModel(read.Approval.ApprovedAt, read.Approval.Reason),
        activation,
        [
            .. read.Version.Sections
                .OrderBy(section => section.SectionKey)
                .ThenBy(section => section.ChannelKey, StringComparer.Ordinal)
                .Select(section => new BrandStyleGuideSectionServiceModel(
                    section.SectionKey,
                    section.ChannelKey.Length == 0 ? null : section.ChannelKey,
                    section.Body)),
        ],
        [
            .. read.Version.Rules
                .OrderBy(rule => rule.SortOrder)
                .Select(rule => new BrandStyleGuideRuleServiceModel(rule.Kind, rule.Text)),
        ],
        [
            .. read.Citations
                .OrderBy(citation => citation.DocumentId)
                .ThenBy(citation => citation.VersionNumber)
                .Select(citation => new BrandStyleGuideSourceServiceModel(citation.DocumentId, citation.VersionNumber)),
        ]);

    public async Task<OperationResult<BrandStyleGuideServiceModel>> CreateAsync(
        string userId, BrandStyleGuideDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);

        // Each pointer has to resolve to an exact version this workspace owns before anything is written. A
        // pointer is never trusted for being well formed: the id could be another workspace's.
        var resolved = await dataLayer.ResolveSourceVersionsAsync(draft.Sources, cancellationToken);

        var unusable = Enumerable.Range(0, resolved.Count).Where(index => resolved[index] is null).ToList();

        if (unusable.Count > 0)
        {
            return OperationResult<BrandStyleGuideServiceModel>.Failure(OperationError.Validation(
                BrandErrorCodes.GuideSourceUnprocessable,
                "A selected source document version could not be used.",
                unusable.Select(index => ($"SourceDocuments[{index}]", "This document version could not be used."))));
        }

        var now = clock.UtcNow;
        var workspaceId = workspace.WorkspaceId;
        var membershipId = workspace.MembershipId;

        var guide = new BrandStyleGuide
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            DisplayName = draft.DisplayName,
            Purpose = draft.Purpose,
            Status = BrandStyleGuideStatus.Active,
            CreatedAt = now,
            UpdatedAt = now,
            CreatedByMembershipId = membershipId,
            UpdatedByMembershipId = membershipId,
        };

        var version = new BrandStyleGuideVersion
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            BrandStyleGuideId = guide.Id,
            VersionNumber = 1,
            ParentVersionId = null,
            ChangeReason = null,
            CreatedByMembershipId = membershipId,
            CreatedAt = now,
            Sections =
            [
                .. draft.Sections.Select(section => new BrandStyleGuideSection
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = workspaceId,
                    BrandStyleGuideVersionId = Guid.Empty,
                    SectionKey = section.SectionKey,
                    ChannelKey = section.ChannelKey ?? string.Empty,
                    Body = section.Body,
                }),
            ],
            Rules =
            [
                .. draft.Rules.Select((rule, order) => new BrandStyleGuideRule
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = workspaceId,
                    BrandStyleGuideVersionId = Guid.Empty,
                    Kind = rule.Kind,
                    Text = rule.Text,
                    SortOrder = order,
                }),
            ],
            SourceLinks =
            [
                .. resolved.Select(versionId => new BrandStyleGuideSourceLink
                {
                    WorkspaceId = workspaceId,
                    BrandStyleGuideVersionId = Guid.Empty,
                    BrandSourceDocumentVersionId = versionId!.Value,
                }),
            ],
        };

        // The children name their parent through the collections above; setting the key too keeps the rows
        // right however the tracker orders its fix-up.
        foreach (var section in version.Sections) { section.BrandStyleGuideVersionId = version.Id; }
        foreach (var rule in version.Rules) { rule.BrandStyleGuideVersionId = version.Id; }
        foreach (var link in version.SourceLinks) { link.BrandStyleGuideVersionId = version.Id; }

        // Ids and counts only: the guide's name and text are the creator's words, and an audit row is not
        // where they belong.
        var audit = new AuditEntry(
            userId,
            BrandAuditActions.StyleGuideCreated,
            BrandAuditActions.StyleGuideResourceType,
            guide.Id.ToString("D"),
            CorrelationId(),
            $"Created a brand style guide: {version.Sections.Count} section(s), {version.Rules.Count} rule(s), "
                + $"{version.SourceLinks.Count} source(s).",
            BeforeReference: null,
            AfterReference: "1");

        await dataLayer.CreateAsync(guide, version, audit, cancellationToken);

        return OperationResult<BrandStyleGuideServiceModel>.Success(new BrandStyleGuideServiceModel(
            guide.Id,
            guide.DisplayName,
            guide.Purpose,
            guide.Status,
            guide.CreatedAt,
            guide.UpdatedAt,
            BrandConcurrencyToken.From(guide.RowVersion),
            new BrandStyleGuideVersionServiceModel(
                version.Id,
                version.VersionNumber,
                version.ChangeReason,
                version.CreatedAt,
                [.. draft.Sections],
                [.. draft.Rules],
                [.. draft.Sources.Select(source => new BrandStyleGuideSourceServiceModel(
                    source.DocumentId!.Value, source.VersionNumber!.Value))])));
    }

    public async Task<OperationResult<BrandStyleGuideVersionSavedServiceModel>> SaveVersionAsync(
        string userId, Guid guideId, BrandStyleGuideEditDraft edit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(edit);

        if (await dataLayer.ReadForVersionWriteAsync(guideId, cancellationToken) is not { } read)
        {
            // Answered identically to a guide that was never created (tenancy.md).
            return CannotSave(new OperationError(
                BrandErrorCodes.GuideNotFound,
                "There is no such brand style guide.",
                new Dictionary<string, string[]>()));
        }

        if (read.Guide.Status is BrandStyleGuideStatus.Archived)
        {
            // A shelved guide stays readable and comparable; what it does not do is take new content. The
            // remedy is one a client can act on, which is why this is a conflict rather than an absence.
            return CannotSave(new OperationError(
                BrandErrorCodes.GuideArchivedConflict,
                "This brand style guide is archived, so a new version cannot be written to it. Restore it first.",
                new Dictionary<string, string[]>()));
        }

        var working = read.Working;

        // Inside the transaction, which is the whole point: the client read the guide before this request
        // started, and an edit landing in between is exactly the case worth refusing rather than rebasing.
        if (working.VersionNumber != edit.ExpectedWorkingVersionNumber)
        {
            return CannotSave(new OperationError(
                BrandErrorCodes.GuideWorkingVersionConflict,
                $"This guide has been edited since it was opened (this edit was made against version "
                    + $"{edit.ExpectedWorkingVersionNumber}, and the guide is now on {working.VersionNumber}). "
                    + "Read it again and decide from there.",
                new Dictionary<string, string[]>(),
                new Dictionary<string, object?>
                {
                    ["expectedWorkingVersionNumber"] = edit.ExpectedWorkingVersionNumber,
                    ["workingVersionNumber"] = working.VersionNumber,
                }));
        }

        var applied = BrandStyleGuideEditApply.Apply(
            SectionsOf(working), RulesOf(working), CitationsOf(read), edit);

        // The caps are checked on the result rather than on the request, because that is where they can be
        // exceeded: a request adding ten rules is legitimate and a guide holding forty-five is legitimate, and
        // only the sum is not. Refused rather than truncated.
        if (LimitFailures(
                applied.Rules,
                applied.Sections,
                applied.Sources,
                nameof(SaveBrandStyleGuideVersionViewModel.SourceDocuments)).ToList() is { Count: > 0 } exceeded)
        {
            return CannotSave(OperationError.Validation(
                BrandErrorCodes.GuideVersionLimitExceeded,
                "That change would take this guide past one of its limits.",
                exceeded));
        }

        if (!applied.Changed)
        {
            // The edit says what the guide already said — a creator who re-saved without changing anything, or
            // typed a word and typed it back. No version is written and no audit row is recorded; the reply
            // reports the version that still stands.
            return OperationResult<BrandStyleGuideVersionSavedServiceModel>.Success(Saved(
                read.Guide.Id,
                working.VersionNumber,
                applied,
                versionId: null,
                versionNumber: null,
                read.WorkingStaleSourceCount));
        }

        // Every citation is resolved again, including the ones travelling through from the working version: a
        // document cited when that version was written may have been removed since, and a pointer is never
        // trusted for having once been good.
        var resolved = await dataLayer.ResolveSourceVersionsAsync(
            [
                .. applied.Sources.Select(source => new BrandStyleGuideSourceInput
                {
                    DocumentId = source.DocumentId,
                    VersionNumber = source.VersionNumber,
                }),
            ],
            cancellationToken);

        var unusable = Enumerable.Range(0, resolved.Count).Where(index => resolved[index] is null).ToList();

        if (unusable.Count > 0)
        {
            // Named by document and version rather than by index, because the index is into the applied result
            // and not into anything the request listed: the citation at fault may be one the creator inherited
            // from the working version, in which case pointing at their own request would be a lie. The
            // extension is how an editor marks the rows the creator has to drop before they can save.
            return CannotSave(new OperationError(
                BrandErrorCodes.GuideSourceUnprocessable,
                "A source document version this guide cites could not be used.",
                new Dictionary<string, string[]>
                {
                    ["sourceDocuments"] =
                    [
                        unusable.Count == 1
                            ? "One cited document version could not be used."
                            : $"{unusable.Count} cited document versions could not be used.",
                    ],
                },
                new Dictionary<string, object?>
                {
                    ["unusableSources"] = unusable.Select(index => applied.Sources[index]).ToArray(),
                }));
        }

        var version = NextVersion(
            read.Guide.Id, working, applied.Sections, applied.Rules, resolved, edit.ChangeReason);

        // The creator's unsaved copy of this guide goes with the version, in the same save: once their words
        // are a version, "you have unsaved changes" is no longer true, and if this write rolls back the draft
        // they composed from is still there. Staged before the save below rather than deleted after it, which
        // would be a second unit of work able to fail on its own.
        // `workspace.AccountId` rather than the `userId` passed in for the audit row: it is the same account,
        // and taking it from the resolved context is what the edit-session seam writes drafts under, so there
        // is one source for "whose draft" rather than two that could disagree.
        await dataLayer.StageEditSessionRemovalAsync(read.Guide.Id, workspace.AccountId, cancellationToken);

        // Ids and counts, and the creator's change reason is deliberately not among them: it is free text about
        // a private guide, and an audit summary is not where that belongs — the same rule the approval and
        // activation reasons follow. The row says the words are the creator's own, which is what tells it apart
        // from a version written from accepted AI guidance.
        var audit = new AuditEntry(
            userId,
            BrandAuditActions.StyleGuideVersionEdited,
            BrandAuditActions.StyleGuideResourceType,
            read.Guide.Id.ToString("D"),
            CorrelationId(),
            $"Edited version {working.VersionNumber} into version {version.VersionNumber}: "
                + $"{applied.SectionsAdded} section(s) added, {applied.SectionsReplaced} replaced, "
                + $"{applied.SectionsCleared} cleared, {applied.RulesAdded} rule(s) added, "
                + $"{applied.RulesRemoved} removed, {applied.SourcesCited} source(s) newly cited, "
                + $"{applied.SourcesUncited} no longer cited.",
            BeforeReference: Pointer(read.Guide.Id, working.VersionNumber),
            AfterReference: Pointer(read.Guide.Id, version.VersionNumber));

        var staleSourceCount = await dataLayer.AddVersionAsync(version, audit, cancellationToken);

        return OperationResult<BrandStyleGuideVersionSavedServiceModel>.Success(Saved(
            read.Guide.Id, working.VersionNumber, applied, version.Id, version.VersionNumber, staleSourceCount));
    }

    public async Task<OperationResult<BrandStyleGuideApprovalResultServiceModel>> ApproveVersionAsync(
        string userId,
        Guid guideId,
        int versionNumber,
        string? reason,
        CancellationToken cancellationToken)
    {
        if (await dataLayer.ReadForApprovalAsync(guideId, versionNumber, cancellationToken) is not { } read)
        {
            // Answered identically to a guide that was never created (tenancy.md).
            return RefuseApproval(new OperationError(
                BrandErrorCodes.GuideNotFound,
                "There is no such brand style guide.",
                new Dictionary<string, string[]>()));
        }

        if (read.Version is not { } version)
        {
            // The guide is already known to be readable, so naming the route's segment discloses nothing.
            return RefuseApproval(OperationError.Validation(
                BrandErrorCodes.GuideVersionNotFound,
                "There is no such version of this brand style guide.",
                [("versionNumber", $"This guide has no version {versionNumber}.")]));
        }

        // Already approved, so there is nothing to write. Reported as a success rather than a conflict: the
        // caller asked for a state the version is already in, and approval is never withdrawn, so this answer
        // cannot go out of date. The original approver and words, not this request's.
        if (read.Approval is { } existing)
        {
            return OperationResult<BrandStyleGuideApprovalResultServiceModel>.Success(
                new BrandStyleGuideApprovalResultServiceModel(
                    read.Guide.Id,
                    version.Id,
                    version.VersionNumber,
                    existing.ApprovedAt,
                    existing.ApprovedByMembershipId,
                    existing.Reason,
                    AlreadyApproved: true));
        }

        if (read.Guide.Status is BrandStyleGuideStatus.Archived)
        {
            return RefuseApproval(new OperationError(
                BrandErrorCodes.GuideArchivedConflict,
                "This brand style guide is archived, so its versions cannot be approved. Restore it first.",
                new Dictionary<string, string[]>()));
        }

        if (version.Sections.Count == 0 && version.Rules.Count == 0)
        {
            return RefuseApproval(new OperationError(
                BrandErrorCodes.GuideVersionEmptyConflict,
                "This version has nothing in it yet, so there is nothing to approve.",
                new Dictionary<string, string[]>()));
        }

        var now = clock.UtcNow;
        var membershipId = workspace.MembershipId;

        // Ids and version numbers only, as activation's does. No before reference: an approval moves nothing
        // off, it adds a fact about one version.
        var audit = new AuditEntry(
            userId,
            BrandAuditActions.StyleGuideVersionApproved,
            BrandAuditActions.StyleGuideResourceType,
            read.Guide.Id.ToString("D"),
            CorrelationId(),
            $"Approved version {version.VersionNumber} of this brand style guide.",
            null,
            Pointer(read.Guide.Id, version.VersionNumber));

        var write = await dataLayer.ApproveAsync(version.Id, reason, membershipId, now, audit, cancellationToken);

        if (write is BrandStyleGuideApprovalWrite.Conflict)
        {
            return RefuseApproval(new OperationError(
                BrandErrorCodes.GuideVersionApprovalConflict,
                "Another request approved this version at the same moment, so this one changed nothing. Ask "
                    + "again to see the approval that was recorded.",
                new Dictionary<string, string[]>()));
        }

        return OperationResult<BrandStyleGuideApprovalResultServiceModel>.Success(
            new BrandStyleGuideApprovalResultServiceModel(
                read.Guide.Id,
                version.Id,
                version.VersionNumber,
                now,
                membershipId,
                reason,
                AlreadyApproved: false));
    }

    public async Task<OperationResult<BrandStyleGuideActivationResultServiceModel>> ActivateVersionAsync(
        string userId,
        Guid guideId,
        int versionNumber,
        Guid? expectedActiveVersionId,
        string? reason,
        CancellationToken cancellationToken)
    {
        if (await dataLayer.ReadForActivationAsync(guideId, versionNumber, cancellationToken) is not { } read)
        {
            // Answered identically to a guide that was never created (tenancy.md).
            return Refuse(new OperationError(
                BrandErrorCodes.GuideNotFound,
                "There is no such brand style guide.",
                new Dictionary<string, string[]>()));
        }

        if (read.Version is not { } version)
        {
            // The guide is already known to be readable, so naming the route's segment discloses nothing.
            return Refuse(OperationError.Validation(
                BrandErrorCodes.GuideVersionNotFound,
                "There is no such version of this brand style guide.",
                [("versionNumber", $"This guide has no version {versionNumber}.")]));
        }

        // Eligibility first, and in this order, because each of these is a fact about the version the caller
        // named rather than about the state of the workspace: none of them reads differently after a re-read.
        if (read.Guide.Status is BrandStyleGuideStatus.Archived)
        {
            return Refuse(new OperationError(
                BrandErrorCodes.GuideArchivedConflict,
                "This brand style guide is archived, so it cannot be the workspace default. Restore it first.",
                new Dictionary<string, string[]>()));
        }

        if (!read.IsApproved)
        {
            return Refuse(new OperationError(
                BrandErrorCodes.GuideVersionUnapprovedConflict,
                "This version has not been approved, so it cannot be the workspace default.",
                new Dictionary<string, string[]>()));
        }

        if (read.StaleSourceCount > 0)
        {
            return Refuse(new OperationError(
                BrandErrorCodes.GuideVersionStaleConflict,
                "This version cites source documents that have been replaced since it was written, so it cannot "
                    + "be the workspace default. Write a new version from the current sources.",
                new Dictionary<string, string[]>(),

                // The count, so a client can say how much is stale without listing the history again. A
                // figure about this workspace's own guide, citing this workspace's own documents.
                new Dictionary<string, object?> { ["staleSourceCount"] = read.StaleSourceCount }));
        }

        if (version.Sections.Count == 0 && version.Rules.Count == 0)
        {
            return Refuse(new OperationError(
                BrandErrorCodes.GuideVersionEmptyConflict,
                "This version has nothing in it yet, so it cannot be the workspace default.",
                new Dictionary<string, string[]>()));
        }

        // Last, and immediately before the write: the one check that is about the world rather than about
        // what was asked for. Null expected means "this workspace has no default", which is a claim like any
        // other and is true only when there is no row.
        if (read.CurrentDefault?.VersionId != expectedActiveVersionId)
        {
            return Refuse(ActivationConflict(read.CurrentDefault));
        }

        // Already the default, so there is nothing to change. Reported as a success that wrote nothing rather
        // than as a conflict: the caller asked for a state the workspace is already in.
        if (read.CurrentDefault is { } unchanged && unchanged.VersionId == version.Id)
        {
            return OperationResult<BrandStyleGuideActivationResultServiceModel>.Success(
                new BrandStyleGuideActivationResultServiceModel(
                    read.Guide.Id,
                    version.Id,
                    version.VersionNumber,
                    unchanged.ActivatedAt,
                    unchanged.ActivatedByMembershipId,
                    unchanged.Reason,
                    AlreadyActive: true,
                    Replaced: null));
        }

        var previous = read.CurrentDefault;
        var now = clock.UtcNow;
        var membershipId = workspace.MembershipId;

        // Ids and version numbers only. The activator's own words stay on the activation row, because an
        // audit summary has to be safe to display and creator free text is not something this seam can
        // promise that of.
        var audit = new AuditEntry(
            userId,
            BrandAuditActions.StyleGuideActivated,
            BrandAuditActions.StyleGuideResourceType,
            read.Guide.Id.ToString("D"),
            CorrelationId(),
            previous is null
                ? $"Made version {version.VersionNumber} the workspace's default brand style guide. The workspace had none."
                : $"Made version {version.VersionNumber} the workspace's default brand style guide, replacing version "
                    + $"{previous.VersionNumber} of {(previous.GuideId == read.Guide.Id ? "the same guide" : "another guide")}.",
            previous is null ? null : Pointer(previous.GuideId, previous.VersionNumber),
            Pointer(read.Guide.Id, version.VersionNumber));

        // The default as it was read above, so the write path is chosen against the same picture every
        // decision in this activation was made against rather than against a second, later read.
        var write = await dataLayer.ActivateAsync(
            previous, version.Id, reason, membershipId, now, audit, cancellationToken);

        if (write is BrandStyleGuideActivationWrite.Conflict)
        {
            // Another activation committed between the read and the save. Re-read rather than report the
            // picture that just lost, so the refusal names what actually holds the default now.
            return Refuse(ActivationConflict(await dataLayer.ReadWorkspaceDefaultAsync(cancellationToken)));
        }

        return OperationResult<BrandStyleGuideActivationResultServiceModel>.Success(
            new BrandStyleGuideActivationResultServiceModel(
                read.Guide.Id,
                version.Id,
                version.VersionNumber,
                now,
                membershipId,
                reason,
                AlreadyActive: false,
                previous is null
                    ? null
                    : new BrandStyleGuideActivatedVersionServiceModel(
                        previous.GuideId, previous.VersionId, previous.VersionNumber)));
    }

    public async Task<OperationResult<BrandStyleGuideVersionCreatedServiceModel>> CreateVersionFromProposalAsync(
        string userId,
        Guid guideId,
        BrandStyleGuideProposalApplication application,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(application);

        if (await dataLayer.ReadForVersionWriteAsync(guideId, cancellationToken) is not { } read)
        {
            // Answered identically to a guide that was never created (tenancy.md).
            return CannotWrite(new OperationError(
                BrandErrorCodes.GuideNotFound,
                "There is no such brand style guide.",
                new Dictionary<string, string[]>()));
        }

        if (read.Guide.Status is BrandStyleGuideStatus.Archived)
        {
            // A shelved guide stays readable and comparable; what it does not do is take new content. The
            // remedy is one a client can act on, which is why this is a conflict rather than an absence.
            return CannotWrite(new OperationError(
                BrandErrorCodes.GuideArchivedConflict,
                "This brand style guide is archived, so a new version cannot be written to it. Restore it first.",
                new Dictionary<string, string[]>()));
        }

        var working = read.Working;

        // The check that stops a silent rebase, and it is here rather than only in the caller because here is
        // inside the transaction: the caller's own copy of this was read before the transaction opened, and an
        // edit landing in between is exactly the case worth refusing. Both numbers are stated — they are version
        // numbers of the caller's own guide, not secrets, and a creator told only "it moved" cannot tell whether
        // they are one edit or ten behind.
        if (working.VersionNumber != application.ExpectedWorkingVersionNumber)
        {
            return CannotWrite(new OperationError(
                BrandErrorCodes.GuideWorkingVersionConflict,
                $"This guide has been edited since that guidance was composed (composed against version "
                    + $"{application.ExpectedWorkingVersionNumber}, the guide is now on {working.VersionNumber}). "
                    + "Read it again and decide from there.",
                new Dictionary<string, string[]>(),
                new Dictionary<string, object?>
                {
                    ["expectedWorkingVersionNumber"] = application.ExpectedWorkingVersionNumber,
                    ["workingVersionNumber"] = working.VersionNumber,
                }));
        }

        if (ProposalInputFailures(application).ToList() is { Count: > 0 } failures)
        {
            return CannotWrite(OperationError.Validation(
                BrandErrorCodes.GuideInvalidRequest,
                "That guidance could not be written to this guide.",
                failures));
        }

        var merged = BrandStyleGuideVersionMerge.Merge(
            [
                .. working.Sections.Select(section => new BrandStyleGuideSectionServiceModel(
                    section.SectionKey,
                    section.ChannelKey.Length == 0 ? null : section.ChannelKey,
                    section.Body)),
            ],
            [
                .. working.Rules
                    .OrderBy(rule => rule.SortOrder)
                    .Select(rule => new BrandStyleGuideRuleServiceModel(rule.Kind, rule.Text)),
            ],
            [
                .. read.Citations
                    .Select(citation => new BrandStyleGuideSourceServiceModel(citation.DocumentId, citation.VersionNumber)),
            ],
            application);

        // The caps are checked on the merge rather than on the request, because that is where they can be
        // exceeded: thirty accepted rules are legitimate and forty stored ones are legitimate, and only the sum
        // is not. Refused rather than truncated — dropping the tail would discard guidance the creator ticked
        // and report success.
        if (LimitFailures(merged).ToList() is { Count: > 0 } exceeded)
        {
            return CannotWrite(OperationError.Validation(
                BrandErrorCodes.GuideVersionLimitExceeded,
                "That guidance would take this guide past one of its limits.",
                exceeded));
        }

        if (!merged.Changed)
        {
            // The accepted guidance says what the guide already said. No version is written — the same no-op
            // rule a creator's own edit follows — and the caller still records the decision, because the creator
            // did make one.
            return OperationResult<BrandStyleGuideVersionCreatedServiceModel>.Success(
                new BrandStyleGuideVersionCreatedServiceModel(
                    read.Guide.Id,
                    VersionId: null,
                    VersionNumber: null,
                    working.VersionNumber,
                    merged.SectionsAdded,
                    merged.SectionsReplaced,
                    merged.RulesAdded,
                    merged.RulesAlreadyPresent,
                    merged.Sources.Count,
                    read.WorkingStaleSourceCount));
        }

        // Every citation has to resolve to an exact version this workspace owns before anything is written, the
        // way creation resolves its own. A pointer is never trusted for having come from a stored proposal: the
        // document could have been removed since the proposal ran.
        var sources = merged.Sources
            .Select(source => new BrandStyleGuideSourceInput
            {
                DocumentId = source.DocumentId,
                VersionNumber = source.VersionNumber,
            })
            .ToList();

        var resolved = await dataLayer.ResolveSourceVersionsAsync(sources, cancellationToken);
        var unusable = Enumerable.Range(0, resolved.Count).Where(index => resolved[index] is null).ToList();

        if (unusable.Count > 0)
        {
            return CannotWrite(OperationError.Validation(
                BrandErrorCodes.GuideSourceUnprocessable,
                "A source document version this guidance cites could not be used.",
                unusable.Select(index => ($"CitedSources[{index}]", "This document version could not be used."))));
        }

        // The same shape a creator's own edit writes, through the same builder: what differs between the two
        // paths is how the content was arrived at, never how a version is put together.
        var version = NextVersion(
            read.Guide.Id, working, merged.Sections, merged.Rules, resolved, application.ChangeReason);

        // Ids and counts. The proposal id is what makes this row answer "where did this guidance come from"
        // later; no section body and no rule text, because an audit summary is not where a private guide's words
        // belong — and these words were a model's, which makes it worse rather than better.
        var audit = new AuditEntry(
            userId,
            BrandAuditActions.StyleGuideVersionCreatedFromProposal,
            BrandAuditActions.StyleGuideResourceType,
            read.Guide.Id.ToString("D"),
            CorrelationId(),
            $"Wrote version {version.VersionNumber} from accepted AI proposal {application.AiProposalId:D}: "
                + $"{merged.SectionsAdded} section(s) added, {merged.SectionsReplaced} replaced, "
                + $"{merged.RulesAdded} rule(s) added, {merged.SourcesAdded} source(s) newly cited.",
            BeforeReference: Pointer(read.Guide.Id, working.VersionNumber),
            AfterReference: Pointer(read.Guide.Id, version.VersionNumber));

        var staleSourceCount = await dataLayer.AddVersionAsync(version, audit, cancellationToken);

        return OperationResult<BrandStyleGuideVersionCreatedServiceModel>.Success(
            new BrandStyleGuideVersionCreatedServiceModel(
                read.Guide.Id,
                version.Id,
                version.VersionNumber,
                working.VersionNumber,
                merged.SectionsAdded,
                merged.SectionsReplaced,
                merged.RulesAdded,
                merged.RulesAlreadyPresent,
                version.SourceLinks.Count,
                staleSourceCount));
    }

    /// <summary>
    /// What is wrong with the accepted guidance itself, before anything is merged.
    /// </summary>
    /// <remarks>
    /// The same bounds <c>BrandStyleGuideInput.Failures</c> applies to a creator's own typing, applied to text
    /// that arrived through acceptance. A proposal's own limits are tighter than these on a body, so in practice
    /// only a creator's rewrite can exceed one — <c>AiPolicy.ChangeValueMaxLength</c> is four thousand
    /// characters and a rule may hold five hundred. That is exactly why the check is here and not assumed
    /// upstream.
    /// </remarks>
    private static IEnumerable<(string Field, string Message)> ProposalInputFailures(
        BrandStyleGuideProposalApplication application)
    {
        for (var index = 0; index < application.Sections.Count; index++)
        {
            var section = application.Sections[index];
            var path = $"Sections[{index}]";

            if (!BrandStyleGuideInput.Has(section.Body))
            {
                yield return ($"{path}.Body", "A section cannot be blank.");
            }
            else if (section.Body.Length > BrandPolicy.StyleGuideSectionBodyMaxLength)
            {
                yield return ($"{path}.Body",
                    $"A section can be at most {BrandPolicy.StyleGuideSectionBodyMaxLength} characters.");
            }

            if (section.SectionKey is BrandStyleGuideSectionKey.ChannelVariant)
            {
                if (!BrandStyleGuideInput.Has(section.ChannelKey)
                    || !BrandProfileInputChecks.IsChannelKey(section.ChannelKey!))
                {
                    yield return ($"{path}.ChannelKey", "A channel variant needs a channel key, such as instagram.");
                }
            }
            else if (BrandStyleGuideInput.Has(section.ChannelKey))
            {
                yield return ($"{path}.ChannelKey", "Only a channel variant names a channel.");
            }
        }

        for (var index = 0; index < application.Rules.Count; index++)
        {
            var rule = application.Rules[index];

            if (!BrandStyleGuideInput.Has(rule.Text))
            {
                yield return ($"Rules[{index}].Text", "A rule cannot be blank.");
            }
            else if (rule.Text.Length > BrandPolicy.StyleGuideRuleTextMaxLength)
            {
                yield return ($"Rules[{index}].Text",
                    $"A rule can be at most {BrandPolicy.StyleGuideRuleTextMaxLength} characters.");
            }
        }
    }

    /// <summary>Which of the guide's own ceilings the merged version would exceed.</summary>
    private static IEnumerable<(string Field, string Message)> LimitFailures(
        BrandStyleGuideVersionMergeResult merged) =>
        LimitFailures(
            merged.Rules,
            merged.Sections,
            merged.Sources,
            nameof(BrandStyleGuideProposalApplication.CitedSources));

    /// <summary>
    /// Which of the guide's own ceilings a composed version would exceed, whoever composed it.
    /// </summary>
    /// <param name="sourcesField">
    /// What the caller's own request calls its citations, so the refusal names a field the client sent rather
    /// than one from the other write path.
    /// </param>
    private static IEnumerable<(string Field, string Message)> LimitFailures(
        IReadOnlyList<BrandStyleGuideRuleServiceModel> rules,
        IReadOnlyList<BrandStyleGuideSectionServiceModel> sections,
        IReadOnlyList<BrandStyleGuideSourceServiceModel> sources,
        string sourcesField)
    {
        if (rules.Count > BrandPolicy.MaxStyleGuideRules)
        {
            yield return (nameof(BrandStyleGuideProposalApplication.Rules),
                $"This guide would have {rules.Count} do and don't rules, and can have at most "
                    + $"{BrandPolicy.MaxStyleGuideRules}.");
        }

        var variants = sections.Count(section => section.SectionKey is BrandStyleGuideSectionKey.ChannelVariant);

        if (variants > BrandPolicy.MaxStyleGuideChannelVariants)
        {
            yield return (nameof(BrandStyleGuideProposalApplication.Sections),
                $"This guide would have {variants} channel variants, and can have at most "
                    + $"{BrandPolicy.MaxStyleGuideChannelVariants}.");
        }

        if (sources.Count > BrandPolicy.MaxStyleGuideSourceLinks)
        {
            yield return (sourcesField,
                $"This version would cite {sources.Count} source documents, and can cite at most "
                    + $"{BrandPolicy.MaxStyleGuideSourceLinks}.");
        }
    }

    /// <summary>
    /// One further version of a guide, built from its working version: the rows, and their parent key set both
    /// ways.
    /// </summary>
    /// <remarks>
    /// Shared by the two write paths — a creator's own edit and accepted AI guidance — because how a version is
    /// assembled is not where they differ. The children name their parent through the collections, and setting
    /// the key as well keeps the rows right however the change tracker orders its fix-up, as on creation.
    /// </remarks>
    private BrandStyleGuideVersion NextVersion(
        Guid guideId,
        BrandStyleGuideVersion working,
        IReadOnlyList<BrandStyleGuideSectionServiceModel> sections,
        IReadOnlyList<BrandStyleGuideRuleServiceModel> rules,
        IReadOnlyList<Guid?> resolvedSourceVersionIds,
        string? changeReason)
    {
        var workspaceId = workspace.WorkspaceId;

        var version = new BrandStyleGuideVersion
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            BrandStyleGuideId = guideId,
            VersionNumber = working.VersionNumber + 1,
            ParentVersionId = working.Id,
            ChangeReason = changeReason,
            CreatedByMembershipId = workspace.MembershipId,
            CreatedAt = clock.UtcNow,
            Sections =
            [
                .. sections.Select(section => new BrandStyleGuideSection
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = workspaceId,
                    BrandStyleGuideVersionId = Guid.Empty,
                    SectionKey = section.SectionKey,
                    ChannelKey = section.ChannelKey ?? string.Empty,
                    Body = section.Body,
                }),
            ],
            Rules =
            [
                .. rules.Select((rule, order) => new BrandStyleGuideRule
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = workspaceId,
                    BrandStyleGuideVersionId = Guid.Empty,
                    Kind = rule.Kind,
                    Text = rule.Text,
                    SortOrder = order,
                }),
            ],
            SourceLinks =
            [
                .. resolvedSourceVersionIds.Select(versionId => new BrandStyleGuideSourceLink
                {
                    WorkspaceId = workspaceId,
                    BrandStyleGuideVersionId = Guid.Empty,
                    BrandSourceDocumentVersionId = versionId!.Value,
                }),
            ],
        };

        foreach (var section in version.Sections) { section.BrandStyleGuideVersionId = version.Id; }
        foreach (var rule in version.Rules) { rule.BrandStyleGuideVersionId = version.Id; }
        foreach (var link in version.SourceLinks) { link.BrandStyleGuideVersionId = version.Id; }

        return version;
    }

    /// <summary>A version's sections as this module's models, with the stored empty channel back to null.</summary>
    private static IReadOnlyList<BrandStyleGuideSectionServiceModel> SectionsOf(BrandStyleGuideVersion version) =>
        [
            .. version.Sections.Select(section => new BrandStyleGuideSectionServiceModel(
                section.SectionKey,
                section.ChannelKey.Length == 0 ? null : section.ChannelKey,
                section.Body)),
        ];

    /// <summary>A version's rules in their stored order, which is the creator's own.</summary>
    private static IReadOnlyList<BrandStyleGuideRuleServiceModel> RulesOf(BrandStyleGuideVersion version) =>
        [
            .. version.Rules
                .OrderBy(rule => rule.SortOrder)
                .Select(rule => new BrandStyleGuideRuleServiceModel(rule.Kind, rule.Text)),
        ];

    private static IReadOnlyList<BrandStyleGuideSourceServiceModel> CitationsOf(
        BrandStyleGuideVersionWriteTarget read) =>
        [
            .. read.Citations.Select(citation => new BrandStyleGuideSourceServiceModel(
                citation.DocumentId, citation.VersionNumber)),
        ];

    /// <summary>What a save reports, whether it wrote a version or found nothing to write.</summary>
    private static BrandStyleGuideVersionSavedServiceModel Saved(
        Guid guideId,
        int parentVersionNumber,
        BrandStyleGuideEditResult applied,
        Guid? versionId,
        int? versionNumber,
        int staleSourceCount) =>
        new(
            guideId,
            versionId,
            versionNumber,
            parentVersionNumber,
            applied.SectionsAdded,
            applied.SectionsReplaced,
            applied.SectionsCleared,
            applied.RulesAdded,
            applied.RulesRemoved,
            applied.SourcesCited,
            applied.SourcesUncited,
            applied.Sections.Count,
            applied.Rules.Count,
            applied.Sources.Count,
            staleSourceCount);

    private static OperationResult<BrandStyleGuideVersionSavedServiceModel> CannotSave(OperationError error) =>
        OperationResult<BrandStyleGuideVersionSavedServiceModel>.Failure(error);

    private static OperationResult<BrandStyleGuideVersionCreatedServiceModel> CannotWrite(OperationError error) =>
        OperationResult<BrandStyleGuideVersionCreatedServiceModel>.Failure(error);

    /// <summary>A guide version as an audit reference: the guide, because the default may move between them.</summary>
    private static string Pointer(Guid guideId, int versionNumber) =>
        $"{guideId:N}:{versionNumber}";

    /// <summary>
    /// The one refusal in this module that names the state the caller got wrong.
    /// </summary>
    /// <remarks>
    /// Every value is this workspace's own and already reachable: a member can find the active version by
    /// listing each guide's history and reading <c>isActive</c>. What the extensions buy is the case the
    /// listing is awkward for — a default held by a guide other than the one being activated — where a caller
    /// told only "re-read" has nowhere in this route's namespace to read it from.
    /// </remarks>
    private static OperationError ActivationConflict(BrandStyleGuideWorkspaceDefault? current) => new(
        BrandErrorCodes.GuideActivationConflict,
        "This workspace's active brand style guide version is not the one this request expected. Read it again "
            + "and confirm from there.",
        new Dictionary<string, string[]>
        {
            ["expectedActiveVersionId"] = ["This is not the version this workspace is currently using."],
        },
        new Dictionary<string, object?>
        {
            ["activeGuideId"] = current?.GuideId,
            ["activeVersionId"] = current?.VersionId,
            ["activeVersionNumber"] = current?.VersionNumber,
        });

    private static OperationResult<BrandStyleGuideActivationResultServiceModel> Refuse(OperationError error) =>
        OperationResult<BrandStyleGuideActivationResultServiceModel>.Failure(error);

    private static OperationResult<BrandStyleGuideApprovalResultServiceModel> RefuseApproval(OperationError error) =>
        OperationResult<BrandStyleGuideApprovalResultServiceModel>.Failure(error);

    private static Guid CorrelationId()
    {
        // The request's trace id, as on the other brand audit rows, so the two can be searched alike.
        var traceId = System.Diagnostics.Activity.Current?.TraceId;

        return traceId is { } id && id != default ? Guid.ParseExact(id.ToHexString(), "N") : Guid.NewGuid();
    }
}

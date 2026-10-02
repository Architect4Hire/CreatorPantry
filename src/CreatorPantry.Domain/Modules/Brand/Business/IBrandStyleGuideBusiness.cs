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

    private static Guid CorrelationId()
    {
        // The request's trace id, as on the other brand audit rows, so the two can be searched alike.
        var traceId = System.Diagnostics.Activity.Current?.TraceId;

        return traceId is { } id && id != default ? Guid.ParseExact(id.ToHexString(), "N") : Guid.NewGuid();
    }
}

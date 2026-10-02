using CreatorPantry.Domain.Managers.Audit;
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

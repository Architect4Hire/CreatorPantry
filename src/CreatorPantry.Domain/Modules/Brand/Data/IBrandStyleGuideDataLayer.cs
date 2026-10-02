using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Brand.Data.Entities;
using CreatorPantry.Domain.Modules.Brand.Managers;

namespace CreatorPantry.Domain.Modules.Brand.Data;

/// <summary>One version of a guide as stored, with what is attached to it.</summary>
public sealed record BrandStyleGuideVersionRead(
    BrandStyleGuideVersion Version,
    int? ParentVersionNumber,
    BrandStyleGuideApproval? Approval,
    IReadOnlyList<(Guid DocumentId, int VersionNumber)> Citations);

/// <summary>A guide with its working version and, if the workspace default is one of its own, the active one.</summary>
public sealed record BrandStyleGuideRead(
    BrandStyleGuide Guide,
    BrandStyleGuideVersionRead Working,
    BrandStyleGuideVersionRead? Active,
    DateTimeOffset? ActivatedAt,
    string? ActivationReason);

public interface IBrandStyleGuideDataLayer
{
    /// <summary>
    /// Reads one guide of the resolved workspace, or null if there is none — which is also what another
    /// workspace's guide looks like.
    /// </summary>
    Task<BrandStyleGuideRead?> ReadAsync(Guid guideId, CancellationToken cancellationToken);

    /// <summary>
    /// Resolves each cited (document, version number) to the exact version id in the resolved workspace.
    /// An entry is null when it cannot be used, for any reason, and the reasons are not distinguished.
    /// </summary>
    Task<IReadOnlyList<Guid?>> ResolveSourceVersionsAsync(
        IReadOnlyList<BrandStyleGuideSourceInput> sources, CancellationToken cancellationToken);

    /// <summary>
    /// Writes the guide, its first version with every section, rule and source link, and the audit entry in
    /// one save, so none can commit without the others.
    /// </summary>
    Task CreateAsync(
        BrandStyleGuide guide, BrandStyleGuideVersion version, AuditEntry audit, CancellationToken cancellationToken);
}

internal sealed class BrandStyleGuideDataLayer(
    IBrandStyleGuideRepository guides,
    IAuditWriter auditWriter,
    CreatorPantryDbContext context) : IBrandStyleGuideDataLayer
{
    public async Task<BrandStyleGuideRead?> ReadAsync(Guid guideId, CancellationToken cancellationToken)
    {
        if (await guides.FindGuideAsync(guideId, cancellationToken) is not { } guide
            || await guides.FindWorkingVersionAsync(guideId, cancellationToken) is not { } working)
        {
            return null;
        }

        var activeRecord = await guides.FindActiveAsync(guideId, cancellationToken);
        var active = activeRecord is null
            ? null
            : activeRecord.VersionId == working.Id
                ? working
                : await guides.FindVersionAsync(activeRecord.VersionId, cancellationToken);

        var versions = new[] { working, active }.Where(version => version is not null).Select(version => version!).ToList();
        var citations = await guides.FindCitationsAsync([.. versions.Select(version => version.Id).Distinct()], cancellationToken);

        async Task<BrandStyleGuideVersionRead> Attach(BrandStyleGuideVersion version) => new(
            version,
            version.ParentVersionId is { } parent ? await guides.FindVersionNumberAsync(parent, cancellationToken) : null,
            await guides.FindApprovalAsync(version.Id, cancellationToken),
            [.. citations.Where(citation => citation.GuideVersionId == version.Id)
                .Select(citation => (citation.DocumentId, citation.VersionNumber))]);

        return new BrandStyleGuideRead(
            guide,
            await Attach(working),
            active is null ? null : await Attach(active),
            activeRecord?.ActivatedAt,
            activeRecord?.ActivationReason);
    }

    public async Task<IReadOnlyList<Guid?>> ResolveSourceVersionsAsync(
        IReadOnlyList<BrandStyleGuideSourceInput> sources, CancellationToken cancellationToken)
    {
        if (sources.Count == 0)
        {
            return [];
        }

        var found = await guides.FindSourceVersionsAsync(
            [.. sources.Select(source => source.DocumentId!.Value).Distinct()], cancellationToken);

        return
        [
            .. sources.Select(source => found
                .Where(candidate => candidate.DocumentId == source.DocumentId && candidate.VersionNumber == source.VersionNumber)
                .Select(candidate => (Guid?)candidate.VersionId)
                .FirstOrDefault()),
        ];
    }

    public async Task CreateAsync(
        BrandStyleGuide guide, BrandStyleGuideVersion version, AuditEntry audit, CancellationToken cancellationToken)
    {
        guides.Add(guide, version);
        auditWriter.Record(audit);

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            // Nothing may stay staged for the next save on this scope to commit.
            context.ChangeTracker.Clear();
            throw;
        }
    }
}

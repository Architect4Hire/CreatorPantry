using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Brand.Data.Entities;
using CreatorPantry.Domain.Modules.Brand.Managers;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Brand.Data;

/// <summary>A source document version a guide may cite: where it is, and that its document is not removed.</summary>
internal sealed record BrandStyleGuideSourceCandidate(Guid DocumentId, int VersionNumber, Guid VersionId);

/// <summary>A cited source version, named by its document and number.</summary>
internal sealed record BrandStyleGuideCitation(Guid GuideVersionId, Guid DocumentId, int VersionNumber);

/// <summary>The workspace default as it applies to one guide: which of its versions, and what was said.</summary>
internal sealed record BrandStyleGuideActiveRecord(
    Guid VersionId, DateTimeOffset ActivatedAt, string? ActivationReason);

internal interface IBrandStyleGuideRepository
{
    Task<BrandStyleGuide?> FindGuideAsync(Guid guideId, CancellationToken cancellationToken);

    /// <summary>The guide's highest-numbered version with its sections and rules.</summary>
    Task<BrandStyleGuideVersion?> FindWorkingVersionAsync(Guid guideId, CancellationToken cancellationToken);

    Task<BrandStyleGuideVersion?> FindVersionAsync(Guid versionId, CancellationToken cancellationToken);

    /// <summary>The workspace default, only if its version belongs to <paramref name="guideId"/>.</summary>
    Task<BrandStyleGuideActiveRecord?> FindActiveAsync(Guid guideId, CancellationToken cancellationToken);

    Task<BrandStyleGuideApproval?> FindApprovalAsync(Guid versionId, CancellationToken cancellationToken);

    Task<int?> FindVersionNumberAsync(Guid versionId, CancellationToken cancellationToken);

    Task<IReadOnlyList<BrandStyleGuideCitation>> FindCitationsAsync(
        IReadOnlyCollection<Guid> guideVersionIds, CancellationToken cancellationToken);

    /// <summary>Stages a new guide with its first version. Nothing is saved.</summary>
    void Add(BrandStyleGuide guide, BrandStyleGuideVersion version);

    /// <summary>
    /// The versions of the named documents that exist in the resolved workspace and are not removed. The query
    /// filter supplies the workspace, so another workspace's document is simply absent.
    /// </summary>
    Task<IReadOnlyList<BrandStyleGuideSourceCandidate>> FindSourceVersionsAsync(
        IReadOnlyCollection<Guid> documentIds, CancellationToken cancellationToken);
}

internal sealed class BrandStyleGuideRepository(CreatorPantryDbContext context) : IBrandStyleGuideRepository
{
    public Task<BrandStyleGuide?> FindGuideAsync(Guid guideId, CancellationToken cancellationToken) =>
        context.BrandStyleGuides.AsNoTracking().FirstOrDefaultAsync(guide => guide.Id == guideId, cancellationToken);

    public Task<BrandStyleGuideVersion?> FindWorkingVersionAsync(Guid guideId, CancellationToken cancellationToken) =>
        context.BrandStyleGuideVersions
            .AsNoTracking()
            .AsSplitQuery()
            .Include(version => version.Sections)
            .Include(version => version.Rules)
            .Where(version => version.BrandStyleGuideId == guideId)
            .OrderByDescending(version => version.VersionNumber)
            .FirstOrDefaultAsync(cancellationToken);

    public Task<BrandStyleGuideVersion?> FindVersionAsync(Guid versionId, CancellationToken cancellationToken) =>
        context.BrandStyleGuideVersions
            .AsNoTracking()
            .AsSplitQuery()
            .Include(version => version.Sections)
            .Include(version => version.Rules)
            .FirstOrDefaultAsync(version => version.Id == versionId, cancellationToken);

    public Task<BrandStyleGuideActiveRecord?> FindActiveAsync(Guid guideId, CancellationToken cancellationToken) =>
        (from active in context.BrandStyleGuideDefaults.AsNoTracking()
         join version in context.BrandStyleGuideVersions.AsNoTracking()
             on active.BrandStyleGuideVersionId equals version.Id
         where version.BrandStyleGuideId == guideId
         select new BrandStyleGuideActiveRecord(version.Id, active.ActivatedAt, active.Reason))
        .FirstOrDefaultAsync(cancellationToken);

    public Task<BrandStyleGuideApproval?> FindApprovalAsync(Guid versionId, CancellationToken cancellationToken) =>
        context.BrandStyleGuideApprovals.AsNoTracking()
            .FirstOrDefaultAsync(approval => approval.BrandStyleGuideVersionId == versionId, cancellationToken);

    public Task<int?> FindVersionNumberAsync(Guid versionId, CancellationToken cancellationToken) =>
        context.BrandStyleGuideVersions.AsNoTracking()
            .Where(version => version.Id == versionId)
            .Select(version => (int?)version.VersionNumber)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<BrandStyleGuideCitation>> FindCitationsAsync(
        IReadOnlyCollection<Guid> guideVersionIds, CancellationToken cancellationToken) =>
        await (from link in context.BrandStyleGuideSourceLinks.AsNoTracking()
               join version in context.BrandSourceDocumentVersions.AsNoTracking()
                   on link.BrandSourceDocumentVersionId equals version.Id
               where guideVersionIds.Contains(link.BrandStyleGuideVersionId)
               select new BrandStyleGuideCitation(link.BrandStyleGuideVersionId, version.BrandSourceDocumentId, version.VersionNumber))
            .ToListAsync(cancellationToken);

    public void Add(BrandStyleGuide guide, BrandStyleGuideVersion version)
    {
        context.BrandStyleGuides.Add(guide);
        context.BrandStyleGuideVersions.Add(version);
    }

    public async Task<IReadOnlyList<BrandStyleGuideSourceCandidate>> FindSourceVersionsAsync(
        IReadOnlyCollection<Guid> documentIds, CancellationToken cancellationToken) =>
        await (from version in context.BrandSourceDocumentVersions.AsNoTracking()
               join document in context.BrandSourceDocuments.AsNoTracking()
                   on version.BrandSourceDocumentId equals document.Id
               where documentIds.Contains(document.Id) && document.Status != BrandSourceDocumentStatus.Removed
               select new BrandStyleGuideSourceCandidate(document.Id, version.VersionNumber, version.Id))
            .ToListAsync(cancellationToken);
}

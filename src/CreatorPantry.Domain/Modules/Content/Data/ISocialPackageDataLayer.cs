using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Content.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Managers;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Content.Data;

/// <summary>What became of a write to a post package.</summary>
public enum SocialPackageWrite
{
    Saved = 0,

    /// <summary>A slot moved between the read and the write. Nothing was written.</summary>
    Stale = 1,

    /// <summary>
    /// The database refused a row: a pin naming something this workspace does not hold, or a uniqueness rule
    /// a concurrent writer reached first. Nothing was written.
    /// </summary>
    Refused = 2,
}

/// <summary>Composes the post package's persistence operations and owns their transaction boundary.</summary>
public interface ISocialPackageDataLayer
{
    /// <inheritdoc cref="ISocialPackageRepository.FindContextFactsAsync"/>
    Task<SocialContextFacts?> FindContextFactsAsync(Guid contextId, CancellationToken cancellationToken);

    /// <summary>The context's package with what decides each channel's currency, or null when it has none.</summary>
    Task<SocialPackageSnapshot?> FindAsync(Guid contextId, CancellationToken cancellationToken);

    /// <inheritdoc cref="ISocialPackageRepository.FindByContextForUpdateAsync"/>
    Task<SocialPackage?> FindForUpdateAsync(Guid contextId, CancellationToken cancellationToken);

    /// <inheritdoc cref="ISocialPackageRepository.FindLatestRevisionAsync"/>
    Task<SocialRevision?> FindLatestRevisionAsync(Guid channelId, CancellationToken cancellationToken);

    /// <inheritdoc cref="ISocialPackageRepository.FindRevisionAsync"/>
    Task<SocialRevision?> FindRevisionAsync(Guid channelId, Guid revisionId, CancellationToken cancellationToken);

    /// <inheritdoc cref="IContentStalenessRepository.GetLatestRecipeVersionAsync"/>
    Task<LatestRecipeVersionRecord?> FindLatestRecipeVersionAsync(Guid recipeId, CancellationToken cancellationToken);

    /// <inheritdoc cref="ISocialPackageRepository.FindRecipeVersionNumberAsync"/>
    Task<int?> FindRecipeVersionNumberAsync(Guid recipeVersionId, CancellationToken cancellationToken);

    /// <summary>
    /// Saves a package Business created or loaded for update, with the revision and audit entry that belong
    /// to the same move, in one save.
    /// </summary>
    /// <remarks>
    /// One save, one transaction. A slot Business changed goes out as an <c>UPDATE</c> quoting the row version
    /// it was read with, so a decision composed against a slot somebody else has since moved is refused whole
    /// — its revision and its audit entry with it.
    /// </remarks>
    /// <param name="isNew">True when Business created the package rather than loading it.</param>
    Task<SocialPackageWrite> SaveAsync(
        SocialPackage package,
        bool isNew,
        SocialRevision? revision,
        AuditEntry? audit,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="ISocialPackageDataLayer"/>
internal sealed class SocialPackageDataLayer(
    ISocialPackageRepository packages,
    IContentStalenessRepository recipeVersions,
    IAuditWriter auditWriter,
    CreatorPantryDbContext db) : ISocialPackageDataLayer
{
    public Task<SocialContextFacts?> FindContextFactsAsync(Guid contextId, CancellationToken cancellationToken) =>
        packages.FindContextFactsAsync(contextId, cancellationToken);

    public async Task<SocialPackageSnapshot?> FindAsync(Guid contextId, CancellationToken cancellationToken)
    {
        var package = await packages.FindByContextAsync(contextId, cancellationToken);

        if (package is null)
        {
            return null;
        }

        var heads = await packages.ListHeadsAsync(package.Id, cancellationToken);
        var acceptedIds = package.Channels
            .Where(channel => channel.AcceptedRevisionId is not null)
            .Select(channel => channel.AcceptedRevisionId!.Value)
            .ToHashSet();

        var pinned = new Dictionary<Guid, int>();
        var latest = new Dictionary<Guid, int>();

        // Only accepted revisions make a claim of currency, and a package is about one recipe in practice, so
        // these are a read or two rather than one per channel.
        foreach (var revision in heads.Where(head => acceptedIds.Contains(head.Id) && head.RecipeVersionId is not null))
        {
            var versionId = revision.RecipeVersionId!.Value;
            var recipeId = revision.RecipeId!.Value;

            if (!pinned.ContainsKey(versionId)
                && await packages.FindRecipeVersionNumberAsync(versionId, cancellationToken) is { } number)
            {
                pinned[versionId] = number;
            }

            if (!latest.ContainsKey(recipeId)
                && await recipeVersions.GetLatestRecipeVersionAsync(recipeId, cancellationToken) is { } newest)
            {
                latest[recipeId] = newest.VersionNumber;
            }
        }

        return new SocialPackageSnapshot(package, heads, pinned, latest);
    }

    public Task<SocialPackage?> FindForUpdateAsync(Guid contextId, CancellationToken cancellationToken) =>
        packages.FindByContextForUpdateAsync(contextId, cancellationToken);

    public Task<SocialRevision?> FindLatestRevisionAsync(Guid channelId, CancellationToken cancellationToken) =>
        packages.FindLatestRevisionAsync(channelId, cancellationToken);

    public Task<SocialRevision?> FindRevisionAsync(Guid channelId, Guid revisionId, CancellationToken cancellationToken) =>
        packages.FindRevisionAsync(channelId, revisionId, cancellationToken);

    public Task<LatestRecipeVersionRecord?> FindLatestRecipeVersionAsync(Guid recipeId, CancellationToken cancellationToken) =>
        recipeVersions.GetLatestRecipeVersionAsync(recipeId, cancellationToken);

    public Task<int?> FindRecipeVersionNumberAsync(Guid recipeVersionId, CancellationToken cancellationToken) =>
        packages.FindRecipeVersionNumberAsync(recipeVersionId, cancellationToken);

    public async Task<SocialPackageWrite> SaveAsync(
        SocialPackage package,
        bool isNew,
        SocialRevision? revision,
        AuditEntry? audit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(package);

        if (isNew)
        {
            packages.Add(package);
        }

        if (revision is not null)
        {
            packages.Add(revision);
        }

        if (audit is not null)
        {
            auditWriter.Record(audit);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);

            return SocialPackageWrite.Saved;
        }
        catch (DbUpdateConcurrencyException)
        {
            // Cleared rather than detached one by one: the slot, the revision and the audit row are all
            // staged, and any left behind would ride along on this scope's next save.
            db.ChangeTracker.Clear();

            return SocialPackageWrite.Stale;
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();

            return SocialPackageWrite.Refused;
        }
    }
}

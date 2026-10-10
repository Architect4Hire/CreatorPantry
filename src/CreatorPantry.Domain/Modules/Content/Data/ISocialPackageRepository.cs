using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Content.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Managers;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Content.Data;

/// <summary>EF Core access to post packages. Every read is inside the workspace query filter.</summary>
public interface ISocialPackageRepository
{
    /// <summary>The recipes a context names, in the creator's order; null when the context is not visible.</summary>
    Task<SocialContextFacts?> FindContextFactsAsync(Guid contextId, CancellationToken cancellationToken);

    /// <summary>The context's package with its channel slots, untracked, or null when it has none.</summary>
    Task<SocialPackage?> FindByContextAsync(Guid contextId, CancellationToken cancellationToken);

    /// <summary>The same aggregate, tracked, so each slot's row version is the one the database holds.</summary>
    Task<SocialPackage?> FindByContextForUpdateAsync(Guid contextId, CancellationToken cancellationToken);

    /// <summary>Each slot's latest revision and its accepted one, and no others. No-tracking.</summary>
    Task<IReadOnlyList<SocialRevision>> ListHeadsAsync(Guid packageId, CancellationToken cancellationToken);

    /// <summary>The channel's highest-numbered revision, or null when it has none. No-tracking.</summary>
    Task<SocialRevision?> FindLatestRevisionAsync(Guid channelId, CancellationToken cancellationToken);

    /// <summary>One revision of one channel, or null. No-tracking.</summary>
    Task<SocialRevision?> FindRevisionAsync(Guid channelId, Guid revisionId, CancellationToken cancellationToken);

    /// <summary>A recipe version's number, or null when it is not visible to this workspace.</summary>
    Task<int?> FindRecipeVersionNumberAsync(Guid recipeVersionId, CancellationToken cancellationToken);

    /// <summary>Every Accepted channel whose accepted revision is pinned to a version of this recipe. No-tracking.</summary>
    Task<IReadOnlyList<AcceptedSocialPinRecord>> FindAcceptedPinsAsync(Guid recipeId, CancellationToken cancellationToken);

    /// <summary>One slot tracked, for a change the layer above will save; null when not visible.</summary>
    Task<SocialPackageChannel?> GetChannelForUpdateAsync(Guid channelId, CancellationToken cancellationToken);

    /// <summary>Stages a new package and the slots it holds. Nothing is saved.</summary>
    void Add(SocialPackage package);

    /// <summary>Stages a revision. Nothing is saved.</summary>
    void Add(SocialRevision revision);
}

/// <inheritdoc cref="ISocialPackageRepository"/>
internal sealed class SocialPackageRepository(CreatorPantryDbContext context) : ISocialPackageRepository
{
    // No WorkspaceId predicate anywhere below: contexts, packages, slots, revisions and recipe versions are all
    // workspace-owned, so the global query filter scopes every read, and another workspace's rows are
    // indistinguishable from none.
    public async Task<SocialContextFacts?> FindContextFactsAsync(Guid contextId, CancellationToken cancellationToken)
    {
        if (!await context.CreativeContexts.AsNoTracking().AnyAsync(candidate => candidate.Id == contextId, cancellationToken))
        {
            return null;
        }

        var recipeIds = await context.CreativeContextReferences
            .AsNoTracking()
            .Where(reference => reference.CreativeContextId == contextId && reference.RecipeId != null)
            .OrderBy(reference => reference.SortOrder)
            .Select(reference => reference.RecipeId!.Value)
            .ToListAsync(cancellationToken);

        return new SocialContextFacts(recipeIds);
    }

    public Task<SocialPackage?> FindByContextAsync(Guid contextId, CancellationToken cancellationToken) =>
        context.SocialPackages
            .AsNoTracking()
            .Include(package => package.Channels)
            .FirstOrDefaultAsync(package => package.CreativeContextId == contextId, cancellationToken);

    public Task<SocialPackage?> FindByContextForUpdateAsync(Guid contextId, CancellationToken cancellationToken) =>
        context.SocialPackages
            .Include(package => package.Channels)
            .FirstOrDefaultAsync(package => package.CreativeContextId == contextId, cancellationToken);

    public async Task<IReadOnlyList<SocialRevision>> ListHeadsAsync(Guid packageId, CancellationToken cancellationToken) =>
        await (
            from revision in context.SocialRevisions.AsNoTracking()
            join channel in context.SocialPackageChannels.AsNoTracking() on revision.SocialPackageChannelId equals channel.Id
            where channel.SocialPackageId == packageId
                && (revision.Id == channel.AcceptedRevisionId
                    || revision.RevisionNumber == context.SocialRevisions
                        .Where(other => other.SocialPackageChannelId == channel.Id)
                        .Max(other => other.RevisionNumber))
            select revision)
            .ToListAsync(cancellationToken);

    public Task<SocialRevision?> FindLatestRevisionAsync(Guid channelId, CancellationToken cancellationToken) =>
        context.SocialRevisions
            .AsNoTracking()
            .Where(revision => revision.SocialPackageChannelId == channelId)
            .OrderByDescending(revision => revision.RevisionNumber)
            .FirstOrDefaultAsync(cancellationToken);

    public Task<SocialRevision?> FindRevisionAsync(Guid channelId, Guid revisionId, CancellationToken cancellationToken) =>
        context.SocialRevisions
            .AsNoTracking()
            .FirstOrDefaultAsync(
                revision => revision.SocialPackageChannelId == channelId && revision.Id == revisionId, cancellationToken);

    public async Task<int?> FindRecipeVersionNumberAsync(Guid recipeVersionId, CancellationToken cancellationToken) =>
        await context.RecipeVersions
            .AsNoTracking()
            .Where(version => version.Id == recipeVersionId)
            .Select(version => (int?)version.VersionNumber)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<AcceptedSocialPinRecord>> FindAcceptedPinsAsync(
        Guid recipeId, CancellationToken cancellationToken) =>
        await (
            from channel in context.SocialPackageChannels.AsNoTracking()
            where channel.Status == ContentProposalStatus.Accepted
            join revision in context.SocialRevisions.AsNoTracking() on channel.AcceptedRevisionId equals revision.Id
            where revision.RecipeId == recipeId
            join version in context.RecipeVersions.AsNoTracking() on revision.RecipeVersionId equals version.Id
            select new AcceptedSocialPinRecord(channel.Id, revision.Id, version.Id, version.VersionNumber))
            .ToListAsync(cancellationToken);

    public Task<SocialPackageChannel?> GetChannelForUpdateAsync(Guid channelId, CancellationToken cancellationToken) =>
        context.SocialPackageChannels.SingleOrDefaultAsync(channel => channel.Id == channelId, cancellationToken);

    public void Add(SocialPackage package) => context.SocialPackages.Add(package);

    public void Add(SocialRevision revision) => context.SocialRevisions.Add(revision);
}

using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Content.Managers;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Content.Data;

public interface IContentSeoRepository
{
    /// <summary>
    /// The recipe's proposal of the given kind and its accepted revision, or <c>null</c> when none was ever
    /// accepted. One proposal exists per recipe and kind, so the answer is deterministic.
    /// </summary>
    Task<AcceptedContentRecord?> FindAcceptedAsync(Guid recipeId, ContentPackageKind kind, CancellationToken cancellationToken);
}

internal sealed class ContentSeoRepository(CreatorPantryDbContext context) : IContentSeoRepository
{
    // No WorkspaceId predicate: proposals, revisions and recipe versions are workspace-owned, so the global
    // query filter scopes every read, and another workspace's rows are indistinguishable from none.
    public Task<AcceptedContentRecord?> FindAcceptedAsync(
        Guid recipeId, ContentPackageKind kind, CancellationToken cancellationToken) =>
        (
            from proposal in context.ContentProposals.AsNoTracking()
            where proposal.RecipeId == recipeId
                && proposal.Kind == kind
                && proposal.AcceptedRevisionId != null
            join revision in context.ContentRevisions.AsNoTracking() on proposal.AcceptedRevisionId equals revision.Id
            select new AcceptedContentRecord(
                revision.Id, revision.RevisionNumber, proposal.Status, revision.RecipeVersionId, revision.Content))
            .FirstOrDefaultAsync(cancellationToken);
}

/// <remarks>
/// Named for the package it was first written for, but it reads any package kind's accepted revision: the SEO
/// and editorial businesses share it and differ only in the <see cref="ContentPackageKind"/> they ask for, so
/// there is one query to keep correct rather than two that could drift.
/// </remarks>
public interface IContentSeoDataLayer
{
    Task<AcceptedContentRecord?> FindAcceptedAsync(Guid recipeId, ContentPackageKind kind, CancellationToken cancellationToken);
}

internal sealed class ContentSeoDataLayer(IContentSeoRepository repository) : IContentSeoDataLayer
{
    // A read of one row: no transaction to own and no cache worth the staleness risk.
    public Task<AcceptedContentRecord?> FindAcceptedAsync(
        Guid recipeId, ContentPackageKind kind, CancellationToken cancellationToken) =>
        repository.FindAcceptedAsync(recipeId, kind, cancellationToken);
}

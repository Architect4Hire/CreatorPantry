using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using System.Reflection;
using CreatorPantry.Domain.Modules.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Managers.Persistence;

/// <summary>Implemented by any DbContext that applies <see cref="WorkspaceOwnershipConvention"/>.</summary>
internal interface IWorkspaceIdSource
{
    /// <summary>
    /// The resolved workspace id for the current scope, or null when none is available — either no
    /// <see cref="CreatorPantry.Domain.Managers.Persistence.IWorkspaceContext"/> is registered (migrations, unrelated hosts) or one is but has not
    /// resolved a workspace for this request yet.
    /// </summary>
    Guid? CurrentWorkspaceIdOrNull { get; }
}

/// <summary>
/// Applies a global EF Core query filter to every entity implementing <see cref="CreatorPantry.Domain.Managers.Persistence.IWorkspaceOwned"/>,
/// discovered from the model by convention rather than configured entity by entity. A new workspace-owned
/// entity is covered automatically the moment it implements the interface; nothing here names it.
/// </summary>
internal static class WorkspaceOwnershipConvention
{
    /// <param name="dbContext">
    /// The DbContext applying this convention, always passed as <c>this</c> from its own
    /// <c>OnModelCreating</c>. Must be captured with its own concrete type (not boxed to <see cref="DbContext"/>
    /// or wrapped in a delegate): EF Core only re-evaluates a query filter's captured state fresh per query,
    /// instead of caching it once for the model's lifetime, when that state is a direct member access on the
    /// context instance itself.
    /// </param>
    public static void Apply<TContext>(ModelBuilder modelBuilder, TContext dbContext)
        where TContext : DbContext, IWorkspaceIdSource
    {
        var applyToEntity = typeof(WorkspaceOwnershipConvention)
            .GetMethod(nameof(ApplyToEntity), BindingFlags.NonPublic | BindingFlags.Static)!;

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (!typeof(IWorkspaceOwned).IsAssignableFrom(entityType.ClrType))
            {
                continue;
            }

            applyToEntity.MakeGenericMethod(entityType.ClrType, typeof(TContext)).Invoke(null, [modelBuilder, dbContext]);
        }
    }

    private static void ApplyToEntity<TEntity, TContext>(ModelBuilder modelBuilder, TContext dbContext)
        where TEntity : class, IWorkspaceOwned
        where TContext : DbContext, IWorkspaceIdSource
    {
        // The one documented exception (tenancy.md): resolving a workspace means reading WorkspaceMembership
        // by an explicit (WorkspaceId, UserId) predicate the caller already supplies (WorkspaceRepository),
        // before any workspace context exists. A strict filter here would make resolution throw against
        // itself. It is safe to relax only because that lookup never relies on the ambient filter for
        // correctness — it already carries its own explicit predicate. Once a workspace is resolved,
        // membership queries are scoped exactly like every other workspace-owned entity.
        if (typeof(TEntity) == typeof(WorkspaceMembership))
        {
            modelBuilder.Entity<TEntity>().HasQueryFilter(entity =>
                dbContext.CurrentWorkspaceIdOrNull == null || entity.WorkspaceId == dbContext.CurrentWorkspaceIdOrNull);
            return;
        }

        // Every other workspace-owned entity is strict: querying it before resolution throws immediately
        // (the same fail-closed contract IWorkspaceContext itself has) instead of silently returning nothing.
        modelBuilder.Entity<TEntity>().HasQueryFilter(entity => entity.WorkspaceId == RequireWorkspaceId(dbContext.CurrentWorkspaceIdOrNull));
    }

    private static Guid RequireWorkspaceId(Guid? currentWorkspaceId) => currentWorkspaceId
        ?? throw new InvalidOperationException("A workspace-owned entity cannot be queried before the workspace context is resolved.");
}

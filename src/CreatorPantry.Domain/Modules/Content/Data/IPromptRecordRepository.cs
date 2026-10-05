using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Content.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Content.Data;

/// <summary>
/// EF Core access to the prompt library, within the resolved workspace.
/// </summary>
/// <remarks>
/// <para>
/// Two methods, for the two operations that exist. PRM-002's paged list has its own interface entirely
/// (<see cref="IPromptRecordSearchRepository"/>), and the two downloads will each bring their own purpose-built
/// query rather than a general-purpose read being provided here in advance of a caller.
/// </para>
/// <para>
/// No <c>WorkspaceId</c> is taken or set: reads are scoped by the global query filter and the id on an insert is
/// stamped by <c>WorkspaceOwnershipInterceptor</c> from the resolved context, which is the one place it may be
/// set (tenancy.md).
/// </para>
/// </remarks>
public interface IPromptRecordRepository
{
    /// <summary>Stages a new prompt record. Nothing is saved.</summary>
    void Add(PromptRecord record);

    /// <summary>
    /// Reads one prompt of the resolved workspace, or null when this workspace has none with that id.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Null covers both an unknown id and another workspace's prompt, and they are one case rather than
    /// two reported alike.</strong> The global query filter means the neighbour's row is not in the set this
    /// query runs over, so there is nothing here to compare a <c>WorkspaceId</c> against and no branch that
    /// could accidentally answer differently for the two.
    /// </para>
    /// <para>
    /// The entity, read untracked, rather than a projection — the layer above maps it and drops
    /// <c>WorkspaceId</c> and <c>CreatedByMembershipId</c>, which costs two Guids on a single-row key lookup.
    /// <see cref="IPromptRecordSearchRepository"/> projects instead because a hundred-row page is where that
    /// stops being negligible. Untracked because nothing may edit what it returns: the row is
    /// <see cref="IImmutableRecord"/>, so a tracked copy could only ever be staged into an update
    /// <c>ImmutableRecordInterceptor</c> would refuse.
    /// </para>
    /// </remarks>
    Task<PromptRecord?> FindAsync(Guid promptRecordId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IPromptRecordRepository"/>
internal sealed class PromptRecordRepository(CreatorPantryDbContext context) : IPromptRecordRepository
{
    public void Add(PromptRecord record) => context.PromptRecords.Add(record);

    public Task<PromptRecord?> FindAsync(Guid promptRecordId, CancellationToken cancellationToken) =>
        context.PromptRecords
            .AsNoTracking()

            // No WorkspaceId predicate, and adding one would suggest the global query filter is optional. The
            // primary key seek happens inside the filtered set, so another workspace's prompt is not found
            // rather than found and rejected.
            .FirstOrDefaultAsync(prompt => prompt.Id == promptRecordId, cancellationToken);
}

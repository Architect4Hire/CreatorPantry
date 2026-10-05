using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Content.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Managers;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Content.Data;

/// <summary>
/// EF Core access to the prompt library, within the resolved workspace.
/// </summary>
/// <remarks>
/// <para>
/// Three methods, for the four operations that exist. PRM-002's paged list has its own interface entirely
/// (<see cref="IPromptRecordSearchRepository"/>). A query is added here when a read actually needs a different
/// one, not once per route: PRM-004's text download reads three columns, so it brought
/// <see cref="FindTextAsync"/>; PRM-005's JSON export publishes every column the detail route does, so it uses
/// <see cref="FindAsync"/> as it stands. A second full-row read would have been the same query written twice.
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

    /// <summary>
    /// Reads what a plain-text download of one prompt is built from, or null when this workspace has none with
    /// that id.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Null covers an unknown id and another workspace's prompt as the single case
    /// <see cref="FindAsync"/> describes, and for the same reason: the global query filter means the
    /// neighbour's row is not in the set this query runs over.
    /// </para>
    /// <para>
    /// <strong>A projection, where <see cref="FindAsync"/> reads the entity</strong>, because a download
    /// publishes one column. The text, the label the file is named from and the moment that names it apart are
    /// all it selects; <c>GeneratedText</c>, the template triple, <c>WorkspaceId</c> and
    /// <c>CreatedByMembershipId</c> are not read at all rather than read and dropped — which on the two that
    /// never leave the server is worth more than the bytes it saves.
    /// </para>
    /// </remarks>
    Task<PromptTextRecord?> FindTextAsync(Guid promptRecordId, CancellationToken cancellationToken);
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

    public Task<PromptTextRecord?> FindTextAsync(Guid promptRecordId, CancellationToken cancellationToken) =>
        context.PromptRecords
            .AsNoTracking()

            // The same key seek inside the same filtered set, for the same reason — and the projection is why
            // the two Guids this layer must never publish are never selected in the first place.
            .Where(prompt => prompt.Id == promptRecordId)
            .Select(prompt => new PromptTextRecord(prompt.Text, prompt.Label, prompt.CreatedAt))
            .FirstOrDefaultAsync(cancellationToken);
}

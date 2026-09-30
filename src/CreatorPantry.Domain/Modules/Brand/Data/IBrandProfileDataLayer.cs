using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Brand.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Brand.Data;

public interface IBrandProfileDataLayer
{
    /// <inheritdoc cref="IBrandProfileRepository.GetAsync"/>
    Task<BrandProfile?> GetAsync(CancellationToken cancellationToken);

    /// <inheritdoc cref="IBrandProfileRepository.GetForUpdateAsync"/>
    Task<BrandProfile?> GetForUpdateAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Saves a new profile, its first revision and its audit entry as one unit. False when the workspace
    /// already has a profile — the unique index is the authority, so two racing creates cannot both succeed.
    /// </summary>
    Task<bool> CreateAsync(
        BrandProfile profile, BrandProfileRevision revision, AuditEntry audit, CancellationToken cancellationToken);

    /// <summary>
    /// Saves an edited profile, its new revision and its audit entry as one unit. False when the profile moved
    /// on since it was read, whichever guard caught it; nothing is written and nothing is left staged.
    /// </summary>
    Task<bool> UpdateAsync(
        BrandProfile loaded, BrandProfileRevision revision, AuditEntry audit, CancellationToken cancellationToken);
}

internal sealed class BrandProfileDataLayer(
    IBrandProfileRepository profiles,
    IAuditWriter auditWriter,
    CreatorPantryDbContext context) : IBrandProfileDataLayer
{
    public Task<BrandProfile?> GetAsync(CancellationToken cancellationToken) => profiles.GetAsync(cancellationToken);

    public Task<BrandProfile?> GetForUpdateAsync(CancellationToken cancellationToken) =>
        profiles.GetForUpdateAsync(cancellationToken);

    public async Task<bool> CreateAsync(
        BrandProfile profile, BrandProfileRevision revision, AuditEntry audit, CancellationToken cancellationToken)
    {
        profiles.Add(profile);
        profiles.Add(revision);
        auditWriter.Record(audit);

        try
        {
            // One save, one transaction: the profile, its first revision and the audit row commit together or
            // not at all.
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Nothing committed, and nothing may stay staged for the next save on this scope to commit.
            context.ChangeTracker.Clear();

            if (await profiles.ExistsAsync(cancellationToken))
            {
                return false;
            }

            throw;
        }

        return true;
    }

    public async Task<bool> UpdateAsync(
        BrandProfile loaded, BrandProfileRevision revision, AuditEntry audit, CancellationToken cancellationToken)
    {
        // Captured before the save, because a successful save replaces it with the token the server just
        // generated, and this is the value a losing writer needs to recognise that it lost.
        var readWith = loaded.RowVersion;
        var profileId = loaded.Id;

        profiles.Add(revision);
        auditWriter.Record(audit);

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            // Two guards on one race: the row version on the profile's UPDATE, and the unique index on
            // (profile, revision). Either may fire first, so ask the question that matters: has the row this
            // edit was composed against moved on? If so it is a conflict whichever guard caught it.
            context.ChangeTracker.Clear();

            var current = await profiles.CurrentRowVersionAsync(profileId, cancellationToken);
            if (exception is DbUpdateConcurrencyException
                || current is null
                || !current.AsSpan().SequenceEqual(readWith))
            {
                return false;
            }

            throw;
        }

        return true;
    }
}

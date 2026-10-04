using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Brand.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Brand.Data;

/// <summary>What a delete of one's own draft did.</summary>
public enum BrandStyleGuideEditSessionDeleteOutcome
{
    Deleted = 0,

    /// <summary>There was no draft. Discarding what is not there is a success.</summary>
    NothingToDelete = 1,

    /// <summary>Concurrent saves kept the row alive through every attempt. Nothing was removed.</summary>
    Conflict = 2,
}

/// <summary>
/// The guide and the working version a draft is measured against: what a creator would be editing.
/// </summary>
/// <param name="WorkingVersionNumber">The guide's highest version number right now.</param>
public sealed record BrandStyleGuideEditTarget(Guid GuideId, int WorkingVersionNumber);

public interface IBrandStyleGuideEditSessionDataLayer
{
    /// <summary>
    /// The guide the route names with its working version number, or null when the resolved workspace has no
    /// such guide — which is also what another workspace's guide looks like.
    /// </summary>
    /// <remarks>
    /// Numbers only: a draft needs to know which version it was composed against and whether that is still
    /// the latest, and nothing a guide's sections say bears on either. Reading them would be a page of
    /// creator content fetched to answer a question about an integer.
    /// </remarks>
    Task<BrandStyleGuideEditTarget?> ReadTargetAsync(Guid guideId, CancellationToken cancellationToken);

    /// <inheritdoc cref="IBrandStyleGuideEditSessionRepository.GetAsync"/>
    Task<BrandStyleGuideEditSession?> GetAsync(Guid guideId, string userId, CancellationToken cancellationToken);

    /// <inheritdoc cref="IBrandStyleGuideEditSessionRepository.GetForUpdateAsync"/>
    Task<BrandStyleGuideEditSession?> GetForUpdateAsync(
        Guid guideId, string userId, CancellationToken cancellationToken);

    /// <summary>
    /// Saves a new draft. False when the caller already has one for this guide — the unique index is the
    /// authority, so two racing first saves cannot both succeed.
    /// </summary>
    /// <remarks>
    /// <strong>No audit entry, here or anywhere in this seam.</strong> A draft is non-canonical scratch and
    /// autosave writes one every few seconds; a row per keystroke would bury the entries that record what a
    /// workspace actually decided. What a creator keeps is a version, and writing one is audited.
    /// </remarks>
    Task<bool> CreateAsync(BrandStyleGuideEditSession session, CancellationToken cancellationToken);

    /// <summary>
    /// Saves an edited draft. False when the row moved on or vanished since it was read; nothing is written
    /// and nothing is left staged.
    /// </summary>
    Task<bool> UpdateAsync(BrandStyleGuideEditSession loaded, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes the caller's own draft of one guide. Only that row: no version, no guide and no other
    /// creator's draft is read or touched.
    /// </summary>
    Task<BrandStyleGuideEditSessionDeleteOutcome> DeleteAsync(
        Guid guideId, string userId, CancellationToken cancellationToken);
}

internal sealed class BrandStyleGuideEditSessionDataLayer(
    IBrandStyleGuideEditSessionRepository sessions,
    IBrandStyleGuideRepository guides,
    CreatorPantryDbContext context) : IBrandStyleGuideEditSessionDataLayer
{
    public async Task<BrandStyleGuideEditTarget?> ReadTargetAsync(
        Guid guideId, CancellationToken cancellationToken)
    {
        // The guide first, so an unknown one and another workspace's stay the same answer, and only then the
        // version it is on.
        if (await guides.FindGuideAsync(guideId, cancellationToken) is not { } guide
            || await guides.FindWorkingVersionAsync(guideId, cancellationToken) is not { } working)
        {
            return null;
        }

        return new BrandStyleGuideEditTarget(guide.Id, working.VersionNumber);
    }

    public Task<BrandStyleGuideEditSession?> GetAsync(
        Guid guideId, string userId, CancellationToken cancellationToken) =>
        sessions.GetAsync(guideId, userId, cancellationToken);

    public Task<BrandStyleGuideEditSession?> GetForUpdateAsync(
        Guid guideId, string userId, CancellationToken cancellationToken) =>
        sessions.GetForUpdateAsync(guideId, userId, cancellationToken);

    public async Task<bool> CreateAsync(BrandStyleGuideEditSession session, CancellationToken cancellationToken)
    {
        sessions.Add(session);

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            context.ChangeTracker.Clear();

            if (await sessions.ExistsAsync(session.BrandStyleGuideId, session.UserId, cancellationToken))
            {
                return false;
            }

            throw;
        }

        return true;
    }

    public async Task<bool> UpdateAsync(BrandStyleGuideEditSession loaded, CancellationToken cancellationToken)
    {
        var readWith = loaded.RowVersion;
        var sessionId = loaded.Id;

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            context.ChangeTracker.Clear();

            return false;
        }
        catch (DbUpdateException)
        {
            context.ChangeTracker.Clear();

            var current = await sessions.CurrentRowVersionAsync(sessionId, cancellationToken);
            if (current is null || !current.AsSpan().SequenceEqual(readWith))
            {
                return false;
            }

            throw;
        }

        return true;
    }

    public async Task<BrandStyleGuideEditSessionDeleteOutcome> DeleteAsync(
        Guid guideId, string userId, CancellationToken cancellationToken)
    {
        // Two attempts, as the setup session's own delete takes: a concurrent autosave between the read and
        // the delete trips the row version, and the creator's intent — discard this — is still clear, so read
        // again and delete what is there now.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var session = await sessions.GetForUpdateAsync(guideId, userId, cancellationToken);
            if (session is null)
            {
                return BrandStyleGuideEditSessionDeleteOutcome.NothingToDelete;
            }

            sessions.Remove(session);

            try
            {
                await context.SaveChangesAsync(cancellationToken);

                return BrandStyleGuideEditSessionDeleteOutcome.Deleted;
            }
            catch (DbUpdateConcurrencyException)
            {
                context.ChangeTracker.Clear();
            }
        }

        return BrandStyleGuideEditSessionDeleteOutcome.Conflict;
    }
}

using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Brand.Data.Entities;
using CreatorPantry.Domain.Modules.Brand.Managers;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Brand.Data;

public interface IBrandSetupSessionDataLayer
{
    /// <inheritdoc cref="IBrandSetupSessionRepository.GetAsync"/>
    Task<BrandSetupSession?> GetAsync(string userId, CancellationToken cancellationToken);

    /// <inheritdoc cref="IBrandSetupSessionRepository.GetForUpdateAsync"/>
    Task<BrandSetupSession?> GetForUpdateAsync(string userId, CancellationToken cancellationToken);

    /// <summary>
    /// Saves a new session and its audit entry as one unit. False when the user already has one — the unique
    /// index is the authority, so two racing first saves cannot both succeed.
    /// </summary>
    Task<bool> CreateAsync(BrandSetupSession session, AuditEntry audit, CancellationToken cancellationToken);

    /// <summary>
    /// Saves an edited session (and an optional audit entry). False when the row moved on or vanished since it
    /// was read; nothing is written and nothing is left staged.
    /// </summary>
    Task<bool> UpdateAsync(BrandSetupSession loaded, AuditEntry? audit, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes the user's own session row and records the audit entry, atomically. Only that row: nothing else
    /// is read or touched. <see cref="BrandSetupSessionDeleteOutcome.NothingToDelete"/> when there was none
    /// (idempotent); <see cref="BrandSetupSessionDeleteOutcome.Conflict"/> when concurrent writes kept the row
    /// alive through every attempt.
    /// </summary>
    Task<BrandSetupSessionDeleteOutcome> DeleteAsync(string userId, Func<BrandSetupSession, AuditEntry> audit, CancellationToken cancellationToken);
}

internal sealed class BrandSetupSessionDataLayer(
    IBrandSetupSessionRepository sessions,
    IAuditWriter auditWriter,
    CreatorPantryDbContext context) : IBrandSetupSessionDataLayer
{
    public Task<BrandSetupSession?> GetAsync(string userId, CancellationToken cancellationToken) =>
        sessions.GetAsync(userId, cancellationToken);

    public Task<BrandSetupSession?> GetForUpdateAsync(string userId, CancellationToken cancellationToken) =>
        sessions.GetForUpdateAsync(userId, cancellationToken);

    public async Task<bool> CreateAsync(BrandSetupSession session, AuditEntry audit, CancellationToken cancellationToken)
    {
        sessions.Add(session);
        auditWriter.Record(audit);

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            context.ChangeTracker.Clear();

            if (await sessions.ExistsAsync(session.UserId, cancellationToken))
            {
                return false;
            }

            throw;
        }

        return true;
    }

    public async Task<bool> UpdateAsync(BrandSetupSession loaded, AuditEntry? audit, CancellationToken cancellationToken)
    {
        var readWith = loaded.RowVersion;
        var sessionId = loaded.Id;

        if (audit is not null)
        {
            auditWriter.Record(audit);
        }

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

    public async Task<BrandSetupSessionDeleteOutcome> DeleteAsync(
        string userId, Func<BrandSetupSession, AuditEntry> audit, CancellationToken cancellationToken)
    {
        // Two attempts: a concurrent save between the read and the delete trips the row version, and the
        // creator's intent (start over) is still clear, so read again and delete what is there now.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var session = await sessions.GetForUpdateAsync(userId, cancellationToken);
            if (session is null)
            {
                return BrandSetupSessionDeleteOutcome.NothingToDelete;
            }

            sessions.Remove(session);
            auditWriter.Record(audit(session));

            try
            {
                await context.SaveChangesAsync(cancellationToken);

                return BrandSetupSessionDeleteOutcome.Deleted;
            }
            catch (DbUpdateConcurrencyException)
            {
                context.ChangeTracker.Clear();
            }
        }

        return BrandSetupSessionDeleteOutcome.Conflict;
    }
}

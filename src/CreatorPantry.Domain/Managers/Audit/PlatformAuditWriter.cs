using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Time;

namespace CreatorPantry.Domain.Managers.Audit;

internal sealed class PlatformAuditWriter(CreatorPantryDbContext context, IClock clock) : IPlatformAuditWriter
{
    public void Record(PlatformAuditEntry entry)
    {
        context.PlatformAuditLogs.Add(new PlatformAuditLog
        {
            Id = Guid.NewGuid(),
            ActorType = entry.ActorType,
            ActorId = entry.ActorId,
            ActorName = entry.ActorName,
            Action = entry.Action,
            SubjectType = entry.SubjectType,
            SubjectId = entry.SubjectId,
            Reason = entry.Reason,
            BeforeReference = entry.BeforeReference,
            AfterReference = entry.AfterReference,
            CorrelationId = entry.CorrelationId,
            OccurredAt = clock.UtcNow,
        });
    }
}

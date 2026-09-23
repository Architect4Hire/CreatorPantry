using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Time;

namespace CreatorPantry.Domain.Managers.Audit;

internal sealed class AuditWriter(CreatorPantryDbContext context, IClock clock) : IAuditWriter
{
    public void Record(AuditEntry entry)
    {
        context.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            ActorUserId = entry.ActorUserId,
            Action = entry.Action,
            ResourceType = entry.ResourceType,
            ResourceId = entry.ResourceId,
            CorrelationId = entry.CorrelationId,
            OccurredAt = clock.UtcNow,
            Summary = entry.Summary,
            BeforeReference = entry.BeforeReference,
            AfterReference = entry.AfterReference,
        });
    }
}

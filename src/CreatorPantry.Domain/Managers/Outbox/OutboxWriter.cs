using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Time;

namespace CreatorPantry.Domain.Managers.Outbox;

internal sealed class OutboxWriter(CreatorPantryDbContext context, IClock clock) : IOutboxWriter
{
    public void Enqueue(string messageType, string payloadJson, Guid correlationId)
    {
        var now = clock.UtcNow;
        context.OutboxMessages.Add(new OutboxMessage
        {
            Id = Guid.NewGuid(),
            Type = messageType,
            PayloadJson = payloadJson,
            CorrelationId = correlationId,
            CreatedAt = now,
            AvailableAt = now,
            Attempts = 0,
            Status = OutboxMessageStatus.Pending,
        });
    }
}

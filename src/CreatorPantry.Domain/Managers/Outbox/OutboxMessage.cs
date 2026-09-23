
namespace CreatorPantry.Domain.Managers.Outbox;

/// <summary>
/// A durable internal event awaiting delivery to exactly one <see cref="IOutboxMessageHandler"/>. Not
/// workspace-owned: it is generic transport infrastructure, like <see cref="IdempotencyRecord"/> — a
/// workspace-scoped event carries its workspace id inside <see cref="PayloadJson"/>, validated by the
/// handler, not by this row.
/// </summary>
public class OutboxMessage
{
    public Guid Id { get; set; }

    /// <summary>The routing key a handler is registered under (keyed DI).</summary>
    public string Type { get; set; } = string.Empty;

    public string PayloadJson { get; set; } = string.Empty;

    public Guid CorrelationId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Not eligible for dispatch before this instant — the initial enqueue time, or a backoff retry time.</summary>
    public DateTimeOffset AvailableAt { get; set; }

    public int Attempts { get; set; }

    public OutboxMessageStatus Status { get; set; }

    /// <summary>The claim token of the dispatch pass currently holding this message, if <see cref="Status"/> is Leased.</summary>
    public Guid? LeasedBy { get; set; }

    public DateTimeOffset? LeaseExpiresAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public string? LastError { get; set; }
}

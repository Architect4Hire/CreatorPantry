namespace CreatorPantry.Domain.Outbox;

/// <param name="Id">The outbox row's own id — never the correlated domain record's id.</param>
/// <param name="Type">The routing key the handler was registered under.</param>
/// <param name="PayloadJson">Handler-defined envelope. Never a raw secret, token, or credential.</param>
/// <param name="CorrelationId">Ties this delivery back to the request/operation that enqueued it.</param>
/// <param name="Attempt">This delivery's 1-based attempt number.</param>
public sealed record OutboxMessageEnvelope(Guid Id, string Type, string PayloadJson, Guid CorrelationId, int Attempt);

/// <summary>
/// Handles every outbox message registered under one <see cref="OutboxMessageEnvelope.Type"/>. Register with
/// keyed DI: <c>services.AddKeyedScoped&lt;IOutboxMessageHandler, MyHandler&gt;("my.message.type")</c>.
/// </summary>
/// <remarks>
/// Delivery is at-least-once, not exactly-once: a crash between a handler completing its side effect and the
/// dispatcher marking the message Completed redelivers it. Implementations must be idempotent — for example,
/// keyed on <see cref="OutboxMessageEnvelope.CorrelationId"/> or another stable identifier already present in
/// the payload — the same discipline backend.md and the background-job skill require of any durable handler.
/// A concrete handler resolving a workspace-scoped action must resolve and validate its own workspace from
/// the payload (tenancy.md) — the dispatcher itself is workspace-agnostic transport.
/// </remarks>
public interface IOutboxMessageHandler
{
    Task HandleAsync(OutboxMessageEnvelope message, CancellationToken cancellationToken);
}

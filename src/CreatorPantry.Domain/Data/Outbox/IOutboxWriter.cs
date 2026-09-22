namespace CreatorPantry.Domain.Data.Outbox;

/// <summary>Injectable into any DataLayer that also writes domain entities on the same <c>CreatorPantryDbContext</c>.</summary>
public interface IOutboxWriter
{
    /// <summary>
    /// Stages an <see cref="OutboxMessage"/> row on the ambient DbContext without saving, so it commits in
    /// the same <c>SaveChangesAsync</c> as the domain write the caller is already about to save — the
    /// domain write and the event commit together, or neither does. This is the whole "transaction helper":
    /// no separate <c>BeginTransactionAsync</c> is needed because one <c>SaveChangesAsync</c> over multiple
    /// pending entities on one <c>DbContext</c> is already one transaction.
    /// </summary>
    void Enqueue(string messageType, string payloadJson, Guid correlationId);
}

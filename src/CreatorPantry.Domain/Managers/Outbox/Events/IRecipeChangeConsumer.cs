namespace CreatorPantry.Domain.Managers.Outbox.Events;

/// <summary>
/// A module that holds derivatives of a recipe and must react when it gains a version. Implemented by a
/// module's facade and registered as an <see cref="IRecipeChangeConsumer"/>; the Recipes module's outbox
/// handler fans out to every registration.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The workspace is already resolved</strong> for the scope the consumer runs in, by the handler, from
/// validated state. A consumer never resolves one and never reads one from the event.
/// </para>
/// <para>
/// <strong>Must be idempotent and order-independent.</strong> Delivery is at-least-once, and one consumer
/// failing redelivers the event to all of them. Compare against the recipe's latest version when running
/// rather than trusting the event's, and mark, never rewrite or delete (content.md).
/// </para>
/// </remarks>
public interface IRecipeChangeConsumer
{
    Task OnRecipeVersionChangedAsync(RecipeVersionChangedEvent change, CancellationToken cancellationToken);
}

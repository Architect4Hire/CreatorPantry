using CreatorPantry.Domain.Managers.Outbox;
using CreatorPantry.Domain.Managers.Outbox.Events;
using CreatorPantry.Domain.Modules.Tenancy.Facade;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// Delivers <see cref="RecipeVersionChangedEvent"/> to every module that holds derivatives of a recipe, after
/// the recipe's write has committed (NFR-005).
/// </summary>
/// <remarks>
/// <para>
/// <strong>One handler, because the outbox allows one per message type.</strong> It fans out to each registered
/// <see cref="IRecipeChangeConsumer"/> so a new kind of derivative joins by registering a consumer instead of
/// by changing the recipe write or this handler.
/// </para>
/// <para>
/// <strong>A fresh scope per message.</strong> The workspace context is one-shot per scope and the dispatcher
/// shares one scope across a batch, so resolving here would make the second message fail. The scope also keeps
/// one message's tracked state away from the next.
/// </para>
/// <para>
/// <strong>Runs as the service identity,</strong> not as the editor who caused the change: authorship outlives
/// membership, and a reaction that depended on the editor still belonging would leave a derivative falsely
/// current the day they left. The payload's workspace id is a claim; it is validated by resolution, and every
/// consumer then runs under that workspace's query filter, so a tampered recipe id finds nothing.
/// </para>
/// <para>
/// <strong>Failure semantics.</strong> A payload that does not parse, or a consumer that throws, fails the
/// message so the outbox retries it and finally poisons it where it stays visible. Every consumer is tried before
/// any failure is raised, so one broken consumer does not starve the rest — safe because consumers are
/// idempotent. A workspace that no longer exists completes the message: there is nothing left to mark.
/// </para>
/// </remarks>
internal sealed class RecipeVersionChangedOutboxHandler(
    IServiceScopeFactory scopeFactory,
    ILogger<RecipeVersionChangedOutboxHandler> logger) : IOutboxMessageHandler
{
    public async Task HandleAsync(OutboxMessageEnvelope message, CancellationToken cancellationToken)
    {
        var change = RecipeVersionChangedEvent.TryParse(message.PayloadJson)
            ?? throw new InvalidOperationException("The recipe-version-changed payload is malformed.");

        await using var scope = scopeFactory.CreateAsyncScope();

        var resolution = await scope.ServiceProvider
            .GetRequiredService<IWorkspaceResolutionFacade>()
            .ResolveForServiceAsync(change.WorkspaceId, cancellationToken);

        if (!resolution.Succeeded)
        {
            logger.LogWarning(
                "Recipe version change {MessageId} names a workspace that no longer exists; nothing to mark.",
                message.Id);
            return;
        }

        var failures = new List<Exception>();

        foreach (var consumer in scope.ServiceProvider.GetServices<IRecipeChangeConsumer>())
        {
            try
            {
                await consumer.OnRecipeVersionChangedAsync(change, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failures.Add(exception);
            }
        }

        if (failures.Count > 0)
        {
            throw new AggregateException(
                $"{failures.Count} recipe change consumer(s) failed for message {message.Id}.", failures);
        }
    }
}

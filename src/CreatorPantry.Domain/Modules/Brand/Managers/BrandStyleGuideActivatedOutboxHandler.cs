using CreatorPantry.Domain.Managers.Outbox;
using CreatorPantry.Domain.Managers.Outbox.Events;
using CreatorPantry.Domain.Modules.Brand.Facade;
using CreatorPantry.Domain.Modules.Tenancy.Facade;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CreatorPantry.Domain.Modules.Brand.Managers;

/// <summary>
/// Delivers <see cref="BrandStyleGuideActivatedEvent"/> to every module that holds content written under a
/// brand style guide, after the activation has committed (LF.6.1).
/// </summary>
/// <remarks>
/// <para>
/// <c>RecipeVersionChangedOutboxHandler</c>'s shape, for its reasons: one handler per message type fanning out
/// to each registered <see cref="IBrandVoiceChangeConsumer"/>; a fresh scope per message, because the workspace
/// context is one-shot per scope; and the service identity rather than whoever activated the guide.
/// </para>
/// <para>
/// <strong>Failure semantics.</strong> A payload that does not parse, or a consumer that throws, fails the
/// message so the outbox retries it and finally poisons it where it stays visible. Every consumer is tried
/// before any failure is raised — safe because consumers are idempotent. A workspace that no longer exists
/// completes the message: there is nothing left to mark.
/// </para>
/// </remarks>
internal sealed class BrandStyleGuideActivatedOutboxHandler(
    IServiceScopeFactory scopeFactory,
    ILogger<BrandStyleGuideActivatedOutboxHandler> logger) : IOutboxMessageHandler
{
    public async Task HandleAsync(OutboxMessageEnvelope message, CancellationToken cancellationToken)
    {
        var change = BrandStyleGuideActivatedEvent.TryParse(message.PayloadJson)
            ?? throw new InvalidOperationException("The brand-style-guide-activated payload is malformed.");

        await using var scope = scopeFactory.CreateAsyncScope();

        var resolution = await scope.ServiceProvider
            .GetRequiredService<IWorkspaceResolutionFacade>()
            .ResolveForServiceAsync(change.WorkspaceId, cancellationToken);

        if (!resolution.Succeeded)
        {
            logger.LogWarning(
                "Brand style guide activation {MessageId} names a workspace that no longer exists; nothing to mark.",
                message.Id);
            return;
        }

        // The active version as it stands now, never the one the event named: delivery is at-least-once and
        // unordered, so an old activation replayed after a newer one must be decided against the newer. A read
        // that fails is a failed delivery, retried — treating "could not read" as "no guide" would mark every
        // piece written under the active one.
        var active = await scope.ServiceProvider
            .GetRequiredService<IBrandStyleGuideFacade>()
            .GetActiveAsync(cancellationToken);

        if (!active.Succeeded)
        {
            throw new InvalidOperationException(
                $"The active brand style guide could not be read for message {message.Id} ({active.Error?.Code}).");
        }

        var activeVersionId = active.Value?.Version.Id;
        var failures = new List<Exception>();

        foreach (var consumer in scope.ServiceProvider.GetServices<IBrandVoiceChangeConsumer>())
        {
            try
            {
                await consumer.OnActiveStyleGuideChangedAsync(activeVersionId, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failures.Add(exception);
            }
        }

        if (failures.Count > 0)
        {
            throw new AggregateException(
                $"{failures.Count} brand voice change consumer(s) failed for message {message.Id}.", failures);
        }
    }
}

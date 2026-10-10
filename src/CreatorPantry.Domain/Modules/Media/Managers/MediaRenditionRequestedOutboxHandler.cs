using CreatorPantry.Domain.Managers.Outbox;
using CreatorPantry.Domain.Modules.Media.Business;
using CreatorPantry.Domain.Modules.Media.Facade;
using CreatorPantry.Domain.Modules.Tenancy.Facade;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CreatorPantry.Domain.Modules.Media.Managers;

/// <summary>
/// Delivers one rendition request: resolves the workspace it names, then lets that workspace's own facade
/// make the renditions (B-28, AF.5.5).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The workspace is validated before anything of it is read</strong> (tenancy.md). The payload's
/// workspace id goes through the same resolution every background path uses, in a scope of its own because
/// resolution is one-shot per scope; the picture is then read under that workspace's query filter and its
/// storage prefix. A message naming a workspace that is gone, or a picture that workspace does not have,
/// does nothing and is done.
/// </para>
/// <para>
/// <strong>Retries are the outbox's.</strong> Anything thrown here is retried with its backoff. On the
/// delivery the outbox will not repeat, the facade is told so and records the picture as not compressed
/// instead of throwing, which is what keeps a picture from being attempted for ever.
/// </para>
/// <para>
/// Nothing is logged but identifiers: no key, no checksum and no message from an exception. The same
/// goes for what a failure leaves on the outbox row — see the rethrow below.
/// </para>
/// </remarks>
internal sealed class MediaRenditionRequestedOutboxHandler(
    IServiceScopeFactory scopeFactory,
    ILogger<MediaRenditionRequestedOutboxHandler> logger) : IOutboxMessageHandler
{
    public async Task HandleAsync(OutboxMessageEnvelope message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        var request = MediaRenditionRequestedEvent.TryParse(message.PayloadJson)
            ?? throw new InvalidOperationException("The rendition-requested payload is malformed.");

        await using var scope = scopeFactory.CreateAsyncScope();

        var resolution = await scope.ServiceProvider
            .GetRequiredService<IWorkspaceResolutionFacade>()
            .ResolveForServiceAsync(request.WorkspaceId, cancellationToken);

        if (!resolution.Succeeded)
        {
            logger.LogWarning(
                "Rendition request {MessageId} names a workspace that no longer exists; nothing to make.",
                message.Id);

            return;
        }

        MediaRenditionProduceSummary summary;

        try
        {
            summary = await scope.ServiceProvider
                .GetRequiredService<IMediaRenditionFacade>()
                .ProduceAsync(request.Source, message.Attempt >= OutboxPolicy.MaxAttempts, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The dispatcher stores a failure's message on the outbox row. Whatever failed below — storage,
            // the database, a codec — its message is not this application's to vouch for and may name a
            // host or a key, so what is rethrown says only what kind of failure it was. The original
            // travels as the inner exception, for a trace, and is not what gets stored.
            throw new InvalidOperationException(
                $"The rendition request could not be completed ({exception.GetType().Name}).", exception);
        }

        if (summary.Ready > 0 || summary.NotCompressed > 0)
        {
            logger.LogInformation(
                "Rendition request {MessageId} for workspace {WorkspaceId}: {Ready} stored, {NotCompressed} not compressed.",
                message.Id,
                request.WorkspaceId,
                summary.Ready,
                summary.NotCompressed);
        }
    }
}

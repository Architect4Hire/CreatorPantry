using System.Text.Json;

namespace CreatorPantry.Domain.Managers.Outbox.Events;

/// <summary>
/// The outbox payload announcing that a workspace's active brand style guide version has changed, so content
/// written under another one can be re-examined (LF.6.1). Ids only: never a word of a guide, and never a secret.
/// </summary>
/// <remarks>
/// <para>
/// In the shared kernel for the reason <see cref="RecipeVersionChangedEvent"/> is: the module that writes it
/// (Brand) and every module that reacts to it may not reference each other's types.
/// </para>
/// <para>
/// <see cref="WorkspaceId"/> is a <em>claim</em>, not an authority. The handler resolves and validates the
/// workspace through the tenancy facade before any consumer runs (tenancy.md), and a consumer never reads it
/// from here.
/// </para>
/// <para>
/// Consumers should not trust <see cref="BrandStyleGuideVersionId"/> as "the active one": delivery is
/// at-least-once and may be out of order, so a consumer compares against the workspace's active version as it
/// stands when it runs.
/// </para>
/// </remarks>
public sealed record BrandStyleGuideActivatedEvent(
    Guid WorkspaceId,
    Guid BrandStyleGuideVersionId,
    Guid InitiatedByMembershipId)
{
    /// <summary>The routing key the handler is registered under.</summary>
    public const string MessageType = "brand.style-guide-activated";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public string Serialize() => JsonSerializer.Serialize(this, Options);

    /// <summary>The event, or <c>null</c> when the payload is not one — which the handler treats as poison.</summary>
    public static BrandStyleGuideActivatedEvent? TryParse(string payloadJson)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<BrandStyleGuideActivatedEvent>(payloadJson, Options);

            return parsed is not null && parsed.WorkspaceId != Guid.Empty && parsed.BrandStyleGuideVersionId != Guid.Empty
                ? parsed
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// A module that holds content written under a brand style guide and must react when the workspace's active
/// version changes. Implemented by a module's facade and registered as an
/// <see cref="IBrandVoiceChangeConsumer"/>; the Brand module's outbox handler fans out to every registration.
/// </summary>
/// <remarks>
/// <para>
/// The same contract as <see cref="IRecipeChangeConsumer"/>: the workspace is already resolved for the scope
/// the consumer runs in, and a consumer is idempotent and order-independent, and marks, never rewrites or
/// deletes (content.md).
/// </para>
/// <para>
/// <strong>A consumer is handed the active version, not the event.</strong> The Brand module's handler reads
/// the workspace's active version when the message is delivered and passes that, so a consumer cannot decide
/// against a version an old or replayed event named, and no consuming module has to reach back into the brand
/// module to ask.
/// </para>
/// </remarks>
public interface IBrandVoiceChangeConsumer
{
    /// <param name="activeVersionId">
    /// The workspace's active brand style guide version as it stands now, or null when it has none.
    /// </param>
    Task OnActiveStyleGuideChangedAsync(Guid? activeVersionId, CancellationToken cancellationToken);
}

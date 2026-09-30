using System.Text.Json;

namespace CreatorPantry.Domain.Managers.Outbox.Events;

/// <summary>Why a recipe gained a new immutable version.</summary>
public enum RecipeVersionChangeCause
{
    Edit = 0,
    Restore = 1,
    Approval = 2,
}

/// <summary>
/// The outbox payload announcing that a recipe has a new version, so derivatives pinned to an older one can be
/// re-examined. Ids only: never recipe content, and never a secret.
/// </summary>
/// <remarks>
/// <para>
/// In the shared kernel because the module that writes it (Recipes) and every module that reacts to it may not
/// reference each other's types; a record of identifiers is the one thing both may name.
/// </para>
/// <para>
/// <see cref="WorkspaceId"/> is a <em>claim</em>, not an authority. The handler resolves and validates the
/// workspace through the tenancy facade before any consumer runs (tenancy.md), and a consumer never reads it
/// from here.
/// </para>
/// <para>
/// Consumers should not trust <see cref="RecipeVersionId"/> as "the latest": delivery is at-least-once and may
/// be out of order, so a consumer compares against the recipe's latest version as it stands when it runs.
/// </para>
/// </remarks>
public sealed record RecipeVersionChangedEvent(
    Guid WorkspaceId,
    Guid RecipeId,
    Guid RecipeVersionId,
    int VersionNumber,
    RecipeVersionChangeCause Cause,
    Guid InitiatedByMembershipId)
{
    /// <summary>The routing key the handler is registered under.</summary>
    public const string MessageType = "recipe.version-changed";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public string Serialize() => JsonSerializer.Serialize(this, Options);

    /// <summary>The event, or <c>null</c> when the payload is not one — which the handler treats as poison.</summary>
    public static RecipeVersionChangedEvent? TryParse(string payloadJson)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<RecipeVersionChangedEvent>(payloadJson, Options);

            return parsed is not null && parsed.WorkspaceId != Guid.Empty && parsed.RecipeId != Guid.Empty && parsed.RecipeVersionId != Guid.Empty
                ? parsed
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

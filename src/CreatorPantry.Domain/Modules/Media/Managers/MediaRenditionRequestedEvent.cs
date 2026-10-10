using System.Text.Json;
using System.Text.Json.Serialization;

namespace CreatorPantry.Domain.Modules.Media.Managers;

/// <summary>
/// Which stored picture a rendition is of: a staged image, or one version of a library asset.
/// </summary>
public readonly record struct MediaRenditionSource(Guid? GeneratedImageId, Guid? MediaAssetId, int? MediaAssetVersionNumber)
{
    public static MediaRenditionSource ForGeneratedImage(Guid generatedImageId) => new(generatedImageId, null, null);

    public static MediaRenditionSource ForAssetVersion(Guid mediaAssetId, int versionNumber) =>
        new(null, mediaAssetId, versionNumber);

    /// <summary>Exactly one picture, named by real identifiers.</summary>
    public bool IsValid =>
        (GeneratedImageId is { } image && image != Guid.Empty && MediaAssetId is null && MediaAssetVersionNumber is null)
        || (GeneratedImageId is null && MediaAssetId is { } asset && asset != Guid.Empty && MediaAssetVersionNumber >= 1);
}

/// <summary>
/// The request to make a stored picture's renditions (B-28, AF.5.5). Written to the outbox in the
/// transaction that stores the picture's own row, so a picture never exists without one behind it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Identifiers and nothing else.</strong> No key, no checksum, no bytes: everything about the
/// picture is read again by the handler, inside the workspace it resolves for itself.
/// </para>
/// <para>
/// <strong>The workspace id is a claim to be checked, not an authority.</strong> It was captured by the code
/// that stored the picture, and the handler still resolves it through the ordinary tenancy path and reads
/// the picture under that workspace's filter — so a message naming one workspace and another's picture
/// finds nothing (tenancy.md).
/// </para>
/// </remarks>
public sealed record MediaRenditionRequestedEvent(
    Guid WorkspaceId, Guid? GeneratedImageId, Guid? MediaAssetId, int? MediaAssetVersionNumber)
{
    public const string MessageType = "media.rendition-requested";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// A factory rather than a second constructor: the serializer needs exactly one to read a payload back.
    /// </summary>
    public static MediaRenditionRequestedEvent For(Guid workspaceId, MediaRenditionSource source) =>
        new(workspaceId, source.GeneratedImageId, source.MediaAssetId, source.MediaAssetVersionNumber);

    [JsonIgnore]
    public MediaRenditionSource Source => new(GeneratedImageId, MediaAssetId, MediaAssetVersionNumber);

    /// <summary>The picture's own id, which ties a delivery to the picture that caused it.</summary>
    [JsonIgnore]
    public Guid CorrelationId => GeneratedImageId ?? MediaAssetId ?? Guid.Empty;

    public string Serialize() => JsonSerializer.Serialize(this, Options);

    public static MediaRenditionRequestedEvent? TryParse(string payloadJson)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<MediaRenditionRequestedEvent>(payloadJson, Options);

            return parsed is not null && parsed.WorkspaceId != Guid.Empty && parsed.Source.IsValid ? parsed : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

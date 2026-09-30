using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using CreatorPantry.Domain.Modules.Brand.Data.Entities;

namespace CreatorPantry.Domain.Modules.Brand.Managers;

/// <summary>
/// The creator's brand facts at one moment, in the shape <c>BrandProfileRevision.Document</c> stores. Also how
/// Business decides whether a patch changed anything: two snapshots of the same facts serialize identically.
/// </summary>
internal sealed record BrandProfileSnapshot(
    int SchemaVersion,
    string BrandName,
    string? ShortDescription,
    string? DefaultAudience,
    string? Locale,
    string? TimeZoneId,
    IReadOnlyList<string> ChannelKeys,
    IReadOnlyList<BrandLinkSnapshot> Links,
    IReadOnlyList<BrandAssetSnapshot> Assets)
{
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },

        // Stored and compared, never rendered into HTML, so the creator's apostrophes and accents stay
        // readable in the document instead of becoming \u0027 escapes.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static BrandProfileSnapshot From(BrandProfile profile) => new(
        CurrentSchemaVersion,
        profile.BrandName,
        profile.ShortDescription,
        profile.DefaultAudience,
        profile.Locale,
        profile.TimeZoneId,
        [.. profile.ChannelDefaults.OrderBy(item => item.SortOrder).Select(item => item.ChannelKey)],
        [.. profile.Links.OrderBy(link => link.SortOrder).Select(link => new BrandLinkSnapshot(link.Kind, link.Url, link.Label))],
        [.. profile.AssetLinks.OrderBy(link => link.SortOrder).Select(link => new BrandAssetSnapshot(link.MediaAssetId, link.Role))]);

    public string ToJson() => JsonSerializer.Serialize(this, Options);
}

internal sealed record BrandLinkSnapshot(BrandLinkKind Kind, string Url, string? Label);

internal sealed record BrandAssetSnapshot(Guid MediaAssetId, BrandAssetRole Role);

using System.Text.RegularExpressions;
using CreatorPantry.Domain.Managers.Reference;

namespace CreatorPantry.Domain.Modules.Brand.Managers;

/// <summary>
/// The shape rules shared by the create and update validators. Each returns the failures as
/// <c>(Field, Message)</c> so the two validators cannot drift apart.
/// </summary>
internal static partial class BrandProfileInputChecks
{
    [GeneratedRegex("^[A-Za-z]{2,3}(-[A-Za-z0-9]{2,8})*$")]
    private static partial Regex LocalePattern();

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]*$")]
    private static partial Regex ChannelKeyPattern();

    /// <summary>Trimmed, and null when nothing is left. The form every optional text field is stored in.</summary>
    /// <summary>Whether <paramref name="key"/> is shaped like a channel key. Opaque: no catalog is consulted.</summary>
    public static bool IsChannelKey(string key) =>
        key.Length <= BrandPolicy.ChannelKeyMaxLength && ChannelKeyPattern().IsMatch(key);

    public static string? Normalize(string? value)
    {
        var trimmed = value?.Trim();

        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    public static IEnumerable<(string, string)> Name(string? value)
    {
        var name = Normalize(value);

        if (name is null)
        {
            yield return (nameof(CreateBrandProfileViewModel.BrandName), "A brand needs a name.");
        }
        else if (name.Length > BrandPolicy.BrandNameMaxLength)
        {
            yield return (nameof(CreateBrandProfileViewModel.BrandName),
                $"A brand name can be at most {BrandPolicy.BrandNameMaxLength} characters.");
        }
    }

    public static IEnumerable<(string, string)> Text(string field, string? value, int max)
    {
        if (Normalize(value) is { } text && text.Length > max)
        {
            yield return (field, $"This can be at most {max} characters.");
        }
    }

    public static IEnumerable<(string, string)> Locale(string? value)
    {
        if (Normalize(value) is not { } locale)
        {
            yield break;
        }

        if (locale.Length > BrandPolicy.LocaleMaxLength || !LocalePattern().IsMatch(locale))
        {
            yield return (nameof(CreateBrandProfileViewModel.Locale), "Use a language tag such as en-US.");
        }
    }

    /// <summary>Shape only. Whether the zone exists is a domain rule, checked in Business.</summary>
    public static IEnumerable<(string, string)> TimeZone(string? value)
    {
        if (Normalize(value) is { } zone && zone.Length > BrandPolicy.TimeZoneIdMaxLength)
        {
            yield return (nameof(CreateBrandProfileViewModel.TimeZoneId), "That is not a time zone identifier.");
        }
    }

    public static IEnumerable<(string, string)> Channels(
        IReadOnlyList<BrandChannelDefaultInput?>? channels, IContentChannelCatalog catalog)
    {
        if (channels is null)
        {
            yield break;
        }

        const string field = nameof(CreateBrandProfileViewModel.ChannelDefaults);

        if (channels.Count > BrandPolicy.MaxChannelDefaults)
        {
            yield return (field, $"A brand can have at most {BrandPolicy.MaxChannelDefaults} default channels.");
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < channels.Count; index++)
        {
            var key = Normalize(channels[index]?.ChannelKey);
            var path = $"{field}[{index}].ChannelKey";

            if (key is null || key.Length > BrandPolicy.ChannelKeyMaxLength || !ChannelKeyPattern().IsMatch(key))
            {
                yield return (path, "Use a lowercase channel key such as blog or newsletter.");
            }
            else if (catalog.Find(key) is null)
            {
                // Shape is fine but no such channel exists. The retired case is Business's to decide, because
                // it depends on what the profile already stores.
                yield return (path, "That is not a channel CreatorPantry knows.");
            }
            else if (!seen.Add(key))
            {
                yield return (path, "Each channel can be listed once.");
            }
        }
    }

    public static IEnumerable<(string, string)> Links(IReadOnlyList<BrandLinkInput?>? links)
    {
        if (links is null)
        {
            yield break;
        }

        const string field = nameof(CreateBrandProfileViewModel.Links);

        if (links.Count > BrandPolicy.MaxLinks)
        {
            yield return (field, $"A brand can have at most {BrandPolicy.MaxLinks} links.");
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < links.Count; index++)
        {
            var link = links[index];

            if (link?.Kind is not { } kind || !Enum.IsDefined(kind))
            {
                yield return ($"{field}[{index}].Kind", "Choose Website or Reference.");
            }

            var url = Normalize(link?.Url);
            if (url is null
                || url.Length > BrandPolicy.UrlMaxLength
                || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || uri.Scheme is not ("http" or "https")
                || !string.IsNullOrEmpty(uri.UserInfo))
            {
                yield return ($"{field}[{index}].Url", "Use an http or https address without a username or password.");
            }
            else if (!seen.Add(url))
            {
                yield return ($"{field}[{index}].Url", "Each link can be listed once.");
            }

            if (Normalize(link?.Label) is { } label && label.Length > BrandPolicy.LinkLabelMaxLength)
            {
                yield return ($"{field}[{index}].Label", $"A label can be at most {BrandPolicy.LinkLabelMaxLength} characters.");
            }
        }
    }

    public static IEnumerable<(string, string)> Assets(IReadOnlyList<BrandAssetInput?>? assets)
    {
        if (assets is null)
        {
            yield break;
        }

        const string field = nameof(CreateBrandProfileViewModel.Assets);

        if (assets.Count > BrandPolicy.MaxAssetLinks)
        {
            yield return (field, $"A brand can have at most {BrandPolicy.MaxAssetLinks} logos.");
            yield break;
        }

        var seen = new HashSet<Guid>();
        var primaries = 0;
        for (var index = 0; index < assets.Count; index++)
        {
            var asset = assets[index];

            if (asset?.MediaAssetId is not { } id || id == Guid.Empty)
            {
                yield return ($"{field}[{index}].MediaAssetId", "Name the media asset.");
            }
            else if (!seen.Add(id))
            {
                yield return ($"{field}[{index}].MediaAssetId", "Each asset can be listed once.");
            }

            if (asset?.Role is not { } role || !Enum.IsDefined(role))
            {
                yield return ($"{field}[{index}].Role", "Choose PrimaryLogo or AlternateLogo.");
            }
            else if (role == BrandAssetRole.PrimaryLogo && ++primaries > 1)
            {
                yield return ($"{field}[{index}].Role", "A brand has one primary logo.");
            }
        }
    }
}

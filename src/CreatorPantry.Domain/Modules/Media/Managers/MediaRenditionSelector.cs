namespace CreatorPantry.Domain.Modules.Media.Managers;

/// <summary>
/// The words a caller uses to choose which encoding of a picture it wants, and the word a response uses to
/// say which it got (B-28, AF.5.6).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Three values and no others:</strong> <c>web</c>, <c>thumbnail</c> and <c>original</c>. A
/// rendition is a <see cref="MediaRenditionPurpose"/>; the original is the absence of one, which is why the
/// selection is a nullable purpose rather than a fourth value on an enum that is stored in a column.
/// </para>
/// <para>
/// <strong>Asking is not getting.</strong> A rendition that does not exist — not made yet, or recorded as
/// not compressed — is answered with the original, and the response says <c>original</c>. That is why the
/// same word list is used for the response header: a caller compares what it asked for with what came back.
/// </para>
/// </remarks>
public static class MediaRenditionSelector
{
    /// <summary>The query parameter that selects an encoding.</summary>
    public const string QueryName = "rendition";

    /// <summary>The response header that states which encoding was served.</summary>
    public const string HeaderName = "X-Rendition";

    public const string Web = "web";

    public const string Thumbnail = "thumbnail";

    public const string Original = "original";

    /// <summary>
    /// Reads a caller's choice. False for a value that is not one of the three; an absent value is the
    /// route's own default, <paramref name="whenAbsent"/>.
    /// </summary>
    /// <param name="selected">The rendition asked for, or null for the original.</param>
    public static bool TryParse(string? value, MediaRenditionPurpose? whenAbsent, out MediaRenditionPurpose? selected)
    {
        switch (value)
        {
            case null:
                selected = whenAbsent;
                return true;

            case Web:
                selected = MediaRenditionPurpose.Web;
                return true;

            case Thumbnail:
                selected = MediaRenditionPurpose.Thumbnail;
                return true;

            case Original:
                selected = null;
                return true;

            default:
                selected = null;
                return false;
        }
    }

    /// <summary>The word for what was served: a rendition's purpose, or the original for none.</summary>
    public static string NameOf(MediaRenditionPurpose? served) => served switch
    {
        MediaRenditionPurpose.Web => Web,
        MediaRenditionPurpose.Thumbnail => Thumbnail,
        _ => Original,
    };
}

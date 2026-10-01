namespace CreatorPantry.Domain.Modules.Brand.Managers;

/// <summary>
/// Limits for the workspace brand profile, shared by request validation and EF configuration.
/// </summary>
public static class BrandPolicy
{
    public const int BrandNameMaxLength = 200;

    public const int ShortDescriptionMaxLength = 500;

    public const int DefaultAudienceMaxLength = 500;

    /// <summary>A BCP-47 language tag; 35 is the length the RFC recommends providing for.</summary>
    public const int LocaleMaxLength = 35;

    /// <summary>An IANA zone identifier such as <c>America/Chicago</c>.</summary>
    public const int TimeZoneIdMaxLength = 64;

    /// <summary>
    /// An opaque key into the channel profiles that own channel constraints. Not a provider name: channel
    /// constraints live in channel profiles and adapters, never here (content.md).
    /// </summary>
    public const int ChannelKeyMaxLength = 64;

    /// <summary>The creator's note on why an edit was made, recorded on its revision.</summary>
    public const int ReasonMaxLength = 500;

    public const int UrlMaxLength = 2048;

    public const int LinkLabelMaxLength = 200;

    /// <summary>Caps per profile, so one request or import cannot attach unbounded rows.</summary>
    public const int MaxChannelDefaults = 20;

    public const int MaxLinks = 20;

    public const int MaxAssetLinks = 10;

    public const int SourceDocumentTitleMaxLength = 200;

    public const int SourceDocumentAudienceMaxLength = 500;

    public const int SourceTagNameMaxLength = 64;

    /// <summary>An RFC 6838 media type: two 127-character names and the slash between them.</summary>
    public const int MediaTypeMaxLength = 255;

    /// <summary>The name the file arrived with. Shown back to the creator; never part of an object key.</summary>
    public const int OriginalFileNameMaxLength = 255;

    /// <summary>A server-generated blob name under the workspace/document/version prefix.</summary>
    public const int ObjectKeyMaxLength = 512;

    /// <summary><c>sha256:</c> plus 64 hex characters.</summary>
    public const int ChecksumMaxLength = 71;

    public const int StyleGuideDisplayNameMaxLength = 200;

    /// <summary>The creator's own words on what a guide is for.</summary>
    public const int StyleGuidePurposeMaxLength = 500;

    public const int StyleGuideSectionBodyMaxLength = 8000;

    public const int StyleGuideRuleTextMaxLength = 500;

    /// <summary>Caps per guide version, so one request or accepted proposal cannot attach unbounded rows.</summary>
    public const int MaxStyleGuideRules = 50;

    public const int MaxStyleGuideChannelVariants = 20;

    public const int MaxStyleGuideSourceLinks = 50;
}

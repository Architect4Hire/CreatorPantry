namespace CreatorPantry.Domain.Modules.Brand.Managers;

/// <summary>
/// The stable key of one prose section of a style-guide version. Comparison and partial acceptance are driven
/// by these keys, so a value is never renumbered or reused. Do/don't rules are not a section: they are an
/// ordered list of their own.
/// </summary>
public enum BrandStyleGuideSectionKey
{
    Voice = 1,

    Tone = 2,

    Tenor = 3,

    WritingStyle = 4,

    Audience = 5,

    PointOfView = 6,

    Vocabulary = 7,

    SentenceRhythm = 8,

    Formatting = 9,

    Storytelling = 10,

    CallsToAction = 11,

    /// <summary>How the guide bends for one channel. The only key a version may hold more than once: one per channel.</summary>
    ChannelVariant = 12,

    BlogGuidance = 13,

    SocialGuidance = 14,

    VisualIdentity = 15,

    PhotographyDirection = 16,

    ImagePromptGuidance = 17,

    /// <summary>What the brand's imagery avoids.</summary>
    NegativeVisualGuidance = 18,

    /// <summary>The creator's own notes on the guide.</summary>
    UserNotes = 19,
}

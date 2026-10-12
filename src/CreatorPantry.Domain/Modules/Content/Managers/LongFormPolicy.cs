namespace CreatorPantry.Domain.Modules.Content.Managers;

/// <summary>How often one role may appear in a document of one kind.</summary>
/// <param name="MaxOccurrences">The most sections that may carry the role, or null when it may repeat freely.</param>
public sealed record LongFormRoleRule(LongFormSectionRole Role, int? MaxOccurrences);

/// <summary>A target length in words. Guidance: missing it is a warning, never a failure.</summary>
public sealed record LongFormWordRange(int Min, int Max);

/// <summary>
/// The deterministic shape rules for long-form (LF.1.3, baseline B-34): which roles each kind allows, the
/// ceilings, and the target lengths. Configuration in code, versioned with the source and reviewed like source.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Limits are code and never prompt text</strong> (content.md). A prompt may be told a target; it is
/// <see cref="LongFormDocumentValidator"/>, after generation, that decides whether a document meets it.
/// </para>
/// <para>
/// <strong>Every ceiling here is editorial.</strong> There is no platform behind a creator's own article, so
/// none of these is anybody's rule but CreatorPantry's. The section, document and word figures are B-34's,
/// approved 2026-10-12; the heading and intent caps were approved with LF.1.3. The channel writing profiles
/// are a separate set of figures and are not read or changed here (B-27).
/// </para>
/// <para>
/// Bump <see cref="Version"/> whenever a figure, a role rule or a counting rule changes — including
/// <see cref="LongFormWordCounter"/>'s — so a result can say which rules produced it.
/// </para>
/// </remarks>
public static class LongFormPolicy
{
    public const string Version = "1.0.0";

    /// <summary>The most one section's body may count, in grapheme clusters of the stored Markdown.</summary>
    public const int SectionMaxCharacters = 3000;

    /// <summary>The most a document's section bodies may count together. Headings are not counted.</summary>
    public const int DocumentMaxCharacters = 20000;

    public const int HeadingMaxCharacters = 120;

    public const int IntentMaxCharacters = 200;

    /// <summary>The most creator-headed <see cref="LongFormSectionRole.Body"/> sections a blog article may hold.</summary>
    public const int MaxBodySections = 4;

    private static readonly LongFormWordRange BlogArticleWords = new(900, 1600);

    private static readonly LongFormWordRange NewsletterIssueWords = new(300, 700);

    private static readonly LongFormRoleRule[] BlogArticleRoles =
    [
        new(LongFormSectionRole.Hook, 1),
        new(LongFormSectionRole.Context, 1),
        new(LongFormSectionRole.WhyItWorks, 1),
        new(LongFormSectionRole.IngredientNotes, 1),
        new(LongFormSectionRole.MethodNarrative, 1),
        new(LongFormSectionRole.Tips, 1),
        new(LongFormSectionRole.Variations, 1),
        new(LongFormSectionRole.Storage, 1),
        new(LongFormSectionRole.Faq, 1),
        new(LongFormSectionRole.Cta, 1),
        new(LongFormSectionRole.Closing, 1),
        new(LongFormSectionRole.Body, MaxBodySections),
    ];

    private static readonly LongFormRoleRule[] NewsletterIssueRoles =
    [
        new(LongFormSectionRole.Greeting, 1),
        new(LongFormSectionRole.Lead, 1),

        // One per source the issue covers (B-35). Binding each to its reference is LF.4.1's.
        new(LongFormSectionRole.Item, null),
        new(LongFormSectionRole.Aside, 1),
        new(LongFormSectionRole.Closing, 1),
        new(LongFormSectionRole.Signoff, 1),
    ];

    /// <summary>The roles a document of this kind may use, in their conventional reading order.</summary>
    public static IReadOnlyList<LongFormRoleRule> RolesFor(LongFormKind kind) => kind switch
    {
        LongFormKind.BlogArticle => BlogArticleRoles,
        LongFormKind.NewsletterIssue => NewsletterIssueRoles,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a long-form kind."),
    };

    /// <summary>The rule for this role in this kind, or null when the kind does not allow the role.</summary>
    public static LongFormRoleRule? FindRole(LongFormKind kind, LongFormSectionRole role) =>
        RolesFor(kind).FirstOrDefault(rule => rule.Role == role);

    public static LongFormWordRange TargetWords(LongFormKind kind) => kind switch
    {
        LongFormKind.BlogArticle => BlogArticleWords,
        LongFormKind.NewsletterIssue => NewsletterIssueWords,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a long-form kind."),
    };
}

namespace CreatorPantry.Domain.Modules.Brand.Managers;

/// <summary>
/// Creates a brand style guide and its first version. Carries no workspace: that comes from the route and the
/// caller's membership.
/// </summary>
/// <remarks>
/// Everything but <see cref="DisplayName"/> is optional, and a blank answer means no row — blank stays blank.
/// The questionnaire and <see cref="Sections"/> are two ways to write the same section rows, so a section named
/// by both is refused rather than merged.
/// </remarks>
public sealed record CreateBrandStyleGuideViewModel
{
    public string? DisplayName { get; init; }

    public string? Purpose { get; init; }

    public BrandStyleGuideQuestionnaire? Questionnaire { get; init; }

    /// <summary>Structured sections for any key, including channel variants.</summary>
    public IReadOnlyList<BrandStyleGuideSectionInput?>? Sections { get; init; }

    /// <summary>Exact source document versions to cite, by document and version number.</summary>
    public IReadOnlyList<BrandStyleGuideSourceInput?>? SourceDocuments { get; init; }
}

/// <summary>The short plain-language questions. Each answer becomes one section or a list of rules.</summary>
public sealed record BrandStyleGuideQuestionnaire
{
    /// <summary>How do you sound? Becomes the <c>Voice</c> section.</summary>
    public string? Voice { get; init; }

    /// <summary>What feeling should readers get? Becomes the <c>Tone</c> section.</summary>
    public string? Tone { get; init; }

    /// <summary>Who are you writing for? Becomes the <c>Audience</c> section.</summary>
    public string? Audience { get; init; }

    /// <summary>Words or phrases you love or avoid. Becomes the <c>Vocabulary</c> section.</summary>
    public string? Vocabulary { get; init; }

    /// <summary>Anything else. Becomes the <c>UserNotes</c> section.</summary>
    public string? Notes { get; init; }

    /// <summary>Things you always do. Each becomes a <c>Do</c> rule.</summary>
    public IReadOnlyList<string?>? AlwaysDo { get; init; }

    /// <summary>Things you never do. Each becomes a <c>Dont</c> rule.</summary>
    public IReadOnlyList<string?>? NeverDo { get; init; }
}

public sealed record BrandStyleGuideSectionInput
{
    public BrandStyleGuideSectionKey? SectionKey { get; init; }

    /// <summary>Required for <c>ChannelVariant</c>, and refused for every other key.</summary>
    public string? ChannelKey { get; init; }

    public string? Body { get; init; }
}

public sealed record BrandStyleGuideSourceInput
{
    public Guid? DocumentId { get; init; }

    public int? VersionNumber { get; init; }
}

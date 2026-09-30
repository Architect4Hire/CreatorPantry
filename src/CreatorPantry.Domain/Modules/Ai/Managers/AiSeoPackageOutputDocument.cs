namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// The structured answer RCPUB-002's SEO package task is held to. The document is also the schema:
/// <see cref="AiSeoPackageOutputSchema.Json"/> exports it for the prompt.
/// </summary>
/// <remarks>
/// <para>
/// <strong>No number anywhere.</strong> There is no field for a search volume, ranking, difficulty, competitor or
/// any other metric, so one has nowhere to live; a figure a model writes into prose anyway is found by
/// <see cref="AiSeoClaimScanner"/>. SEO output is recommendation, never measurement (content.md).
/// </para>
/// <para>
/// <strong>No slug.</strong> A slug is an identifier, and identifier work stays in code
/// (<c>SeoSlug</c>): the handler derives it from the proposed title. The model is not asked for one.
/// </para>
/// <para>
/// Every section is optional because the request chooses which to write, and an empty package is a valid answer.
/// Nothing is repaired: a mis-shaped or out-of-rule answer fails.
/// </para>
/// </remarks>
public sealed record AiSeoPackageOutputDocument
{
    public required string SchemaVersion { get; init; }

    public AiSeoSections Sections { get; init; } = new();

    public IReadOnlyList<AiSeoPackageOutputWarning> Warnings { get; init; } = [];
}

public sealed record AiSeoSections
{
    public AiSeoText? SeoTitle { get; init; }

    public AiSeoText? MetaDescription { get; init; }

    public IReadOnlyList<AiSeoKeyPhrase> KeyPhrases { get; init; } = [];

    public IReadOnlyList<AiSeoAltText> AltText { get; init; } = [];

    public IReadOnlyList<AiSeoInternalLink> InternalLinks { get; init; } = [];
}

public sealed record AiSeoText
{
    public required string Text { get; init; }
}

public sealed record AiSeoKeyPhrase
{
    public required string Phrase { get; init; }
}

/// <summary>
/// A suggested alt text for one of the recipe's linked images. <see cref="Basis"/> says what it was written from,
/// because nothing has looked at the image's pixels: it can rest only on the creator's caption or the recipe's
/// title, never on what the picture shows.
/// </summary>
public sealed record AiSeoAltText
{
    /// <summary>The id the pinned version gives the recipe's asset link. Checked against it afterwards.</summary>
    public required Guid AssetLinkId { get; init; }

    public required string Text { get; init; }

    public required AiSeoAltTextBasis Basis { get; init; }
}

public enum AiSeoAltTextBasis
{
    Unspecified = 0,

    /// <summary>Written from the creator's own caption on the asset link. Requires one.</summary>
    Caption = 1,

    /// <summary>Written from the recipe's title alone: a label, not a description of the image.</summary>
    RecipeTitle = 2,
}

/// <summary>An idea for linking to another of the workspace's own recipes. Never a URL: no page exists to link to.</summary>
public sealed record AiSeoInternalLink
{
    /// <summary>One of the candidate recipes the request supplied. Checked against them afterwards.</summary>
    public required Guid RecipeId { get; init; }

    public required string AnchorText { get; init; }

    public required string Reason { get; init; }
}

public sealed record AiSeoPackageOutputWarning
{
    public required AiWarningKind Kind { get; init; }

    public required string Message { get; init; }

    public AiSeoSection? Section { get; init; }
}

/// <summary>The six sections of an SEO package. Stored and requested by name; append, never renumber.</summary>
public enum AiSeoSection
{
    SeoTitle = 1,
    MetaDescription = 2,
    KeyPhrases = 3,
    Slug = 4,
    AltText = 5,
    InternalLinks = 6,
}

public static class AiSeoSectionCatalog
{
    public static IReadOnlyList<AiSeoSection> All { get; } = Enum.GetValues<AiSeoSection>();

    /// <summary>The sections the model writes: every one but the slug, which code derives.</summary>
    public static IReadOnlyList<AiSeoSection> ModelWritten { get; } = [.. All.Where(section => section is not AiSeoSection.Slug)];

    public static AiSeoSection? Parse(string? name) =>
        Enum.GetNames<AiSeoSection>().FirstOrDefault(known => string.Equals(known, name?.Trim(), StringComparison.OrdinalIgnoreCase)) is { } exact
            ? Enum.Parse<AiSeoSection>(exact)
            : null;

    public static string ToWire(AiSeoSection section) =>
        char.ToLowerInvariant(section.ToString()[0]) + section.ToString()[1..];

    public static IReadOnlySet<AiSeoSection> Present(AiSeoSections sections)
    {
        var present = new HashSet<AiSeoSection>();

        if (sections.SeoTitle is not null) present.Add(AiSeoSection.SeoTitle);
        if (sections.MetaDescription is not null) present.Add(AiSeoSection.MetaDescription);
        if (sections.KeyPhrases.Count > 0) present.Add(AiSeoSection.KeyPhrases);
        if (sections.AltText.Count > 0) present.Add(AiSeoSection.AltText);
        if (sections.InternalLinks.Count > 0) present.Add(AiSeoSection.InternalLinks);

        return present;
    }
}

/// <summary>What one SEO request made available to the model; an answer naming anything outside it is refused.</summary>
/// <param name="Requested">The sections the request asked for.</param>
/// <param name="AssetCaptions">The recipe's image links of the pinned version, by id, with their captions (null when none).</param>
/// <param name="CandidateIds">The recipes offered as link targets.</param>
/// <param name="RecipeId">The recipe itself, which may not be linked to.</param>
public sealed record AiSeoRequestContext(
    IReadOnlySet<AiSeoSection> Requested,
    IReadOnlyDictionary<Guid, string?> AssetCaptions,
    IReadOnlySet<Guid> CandidateIds,
    Guid RecipeId);

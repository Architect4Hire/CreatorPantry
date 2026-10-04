namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// The structured answer one half of a style test drive is held to (11A.24). The document is also the schema:
/// <see cref="AiBrandStyleSamplesOutputSchema.Json"/> exports it for the prompt.
/// </summary>
/// <remarks>
/// <para>
/// <strong>One document per call, not per test drive.</strong> The same shape is returned twice for one
/// operation — once by the call that received no brand context and once by the call that received the selected
/// guide version — and the handler is what pairs them. A single document holding both halves would have meant a
/// single call holding the guide while writing the half that is supposed not to have seen it, which is the one
/// thing this capability must not do.
/// </para>
/// <para>
/// <strong>All three samples are required.</strong> Every other prose capability makes its sections optional
/// because the request chooses them; here the three are fixed and a missing one leaves a column of the
/// comparison empty for no reason a creator could act on. Nothing is repaired — a sample outside its length
/// rules fails rather than being trimmed into something the model never wrote.
/// </para>
/// <para>
/// <strong>There is no field for which guide rule a sample followed, and that absence is deliberate.</strong>
/// The guidance that reached the prompt is what <see cref="BrandContextSelection.SectionKeysFor"/> selected and
/// the package carried, and the server reports it from there. A model-supplied attribution would be an
/// unfalsifiable claim about its own writing — the "this sounds more like you" that 11A.24's RESTRICTION names.
/// </para>
/// </remarks>
public sealed record AiBrandStyleSamplesOutputDocument
{
    public required string SchemaVersion { get; init; }

    public required AiBrandStyleSampleSet Samples { get; init; }

    public IReadOnlyList<AiBrandStyleSampleOutputWarning> Warnings { get; init; } = [];
}

/// <summary>The three pieces one call writes, in the order the screen reads them.</summary>
public sealed record AiBrandStyleSampleSet
{
    public required AiBrandStyleSampleText BlogIntro { get; init; }

    public required AiBrandStyleSampleText SocialCaption { get; init; }

    public required AiBrandStyleSampleText ImagePrompt { get; init; }
}

public sealed record AiBrandStyleSampleText
{
    public required string Text { get; init; }
}

public sealed record AiBrandStyleSampleOutputWarning
{
    public required AiWarningKind Kind { get; init; }

    public required string Message { get; init; }

    /// <summary>The sample this is about, or null for a caution about the whole answer.</summary>
    public AiBrandStyleSample? Sample { get; init; }
}

/// <summary>
/// The three samples, named. Stored as the field name on each change row; append, never renumber.
/// </summary>
/// <remarks>
/// The wire form is what reaches <c>AiStructuredChange.FieldName</c>, so these names are part of the stored
/// shape and of the API's own, not an internal label. Which half of the comparison a row belongs to is the
/// target kind's job — see <see cref="AiChangeTargetKind.BrandStyleSampleWithoutGuide"/>.
/// </remarks>
public enum AiBrandStyleSample
{
    Unspecified = 0,

    BlogIntro = 1,

    SocialCaption = 2,

    ImagePrompt = 3,
}

/// <summary>The three samples as the stored rows and the API name them, and what each is allowed to be.</summary>
public static class AiBrandStyleSampleCatalog
{
    public static IReadOnlyList<AiBrandStyleSample> All { get; } =
        [AiBrandStyleSample.BlogIntro, AiBrandStyleSample.SocialCaption, AiBrandStyleSample.ImagePrompt];

    /// <summary>The field name one sample is stored and served under: the member name, lower-camel.</summary>
    public static string ToWire(AiBrandStyleSample sample) =>
        char.ToLowerInvariant(sample.ToString()[0]) + sample.ToString()[1..];

    public static AiBrandStyleSample? Parse(string? name)
    {
        var trimmed = name?.Trim();

        foreach (var known in All)
        {
            if (string.Equals(ToWire(known), trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return known;
            }
        }

        return null;
    }

    /// <summary>The longest that sample may be. Each has its own, because the three pieces are not alike.</summary>
    public static int MaxLengthOf(AiBrandStyleSample sample) => sample switch
    {
        AiBrandStyleSample.BlogIntro => AiPolicy.StyleSampleBlogIntroMaxLength,
        AiBrandStyleSample.SocialCaption => AiPolicy.StyleSampleSocialCaptionMaxLength,
        AiBrandStyleSample.ImagePrompt => AiPolicy.StyleSampleImagePromptMaxLength,
        _ => 0,
    };

    /// <summary>The text of one sample out of a validated document.</summary>
    public static string TextOf(AiBrandStyleSampleSet samples, AiBrandStyleSample sample)
    {
        ArgumentNullException.ThrowIfNull(samples);

        return sample switch
        {
            AiBrandStyleSample.BlogIntro => samples.BlogIntro.Text,
            AiBrandStyleSample.SocialCaption => samples.SocialCaption.Text,
            AiBrandStyleSample.ImagePrompt => samples.ImagePrompt.Text,
            _ => throw new ArgumentOutOfRangeException(nameof(sample), sample, "Not one of the three samples."),
        };
    }
}

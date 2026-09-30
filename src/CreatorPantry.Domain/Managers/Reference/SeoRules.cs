using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace CreatorPantry.Domain.Managers.Reference;

/// <summary>
/// The length and format rules SEO copy is held to (RCPUB-002), as configuration rather than prompt text.
/// </summary>
/// <remarks>
/// <para>
/// content.md puts channel constraints in profiles and adapters, not scattered through prompts. Channel
/// profiles are Phase 12.1; until they exist this is the one place search-result copy limits live, bound from
/// <c>Content:Seo</c> and defaulted to the conventional search-result limits. The validator enforces them and
/// the prompt is told them, so a limit has one definition.
/// </para>
/// <para>
/// <see cref="Version"/> is computed from the limits, so changing any one changes it. A request pins the version
/// it was made under, and the handler refuses to run against a different one: a package is always judged by the
/// rules the creator's request named, and a rule change cannot silently re-judge work already queued. Nothing has
/// to remember to bump a number.
/// </para>
/// <para>
/// Lengths are counted in characters as a reader counts them (an emoji or an accented letter is one), on the
/// string as written. Pixel width, which search engines truncate on, is not computed and is not claimed.
/// </para>
/// <para>
/// <strong>Every host that takes or runs a request must bind the same <c>Content:Seo</c> configuration.</strong>
/// The API pins the version it sees and the worker refuses any other, so two hosts configured differently make
/// every package fail with both versions named in the message. That is the safe failure, and a visible one; the
/// fix is to configure the hosts alike.
/// </para>
/// </remarks>
public sealed class SeoRules
{
    public const string SectionName = "Content:Seo";

    /// <summary>
    /// Revision of the rules that are code rather than configuration — the alt-text format, the phrase format, the
    /// scanner's lexicons, the fixed item caps. Part of <see cref="Version"/>, so a change to any of them is a new
    /// version and queued work is not silently re-judged. Bump it whenever the validator or scanner changes what
    /// it accepts or reports.
    /// </summary>
    public const int Revision = 2;

    public int TitleMinLength { get; set; } = 10;

    public int TitleMaxLength { get; set; } = 60;

    public int MetaDescriptionMinLength { get; set; } = 50;

    public int MetaDescriptionMaxLength { get; set; } = 160;

    public int KeyPhraseMinCount { get; set; } = 3;

    public int KeyPhraseMaxCount { get; set; } = 8;

    public int KeyPhraseMaxWords { get; set; } = 5;

    public int KeyPhraseMinLength { get; set; } = 2;

    public int KeyPhraseMaxLength { get; set; } = 40;

    public int AltTextMaxLength { get; set; } = 125;

    public int AnchorTextMaxLength { get; set; } = 60;

    public int MaxInternalLinks { get; set; } = 5;

    public int SlugMaxLength { get; set; } = 60;

    /// <summary>Identifies these limits exactly: the same limits always give the same value, any change gives another.</summary>
    public string Version
    {
        get
        {
            var limits = string.Join(
                '|',
                Revision,
                TitleMinLength, TitleMaxLength, MetaDescriptionMinLength, MetaDescriptionMaxLength, KeyPhraseMinCount,
                KeyPhraseMaxCount, KeyPhraseMaxWords, KeyPhraseMinLength, KeyPhraseMaxLength, AltTextMaxLength,
                AnchorTextMaxLength, MaxInternalLinks, SlugMaxLength);

            return "seo.rules/1-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(limits)))[..8];
        }
    }

    /// <summary>The rules from configuration over the defaults. Refuses to start on limits that cannot be met.</summary>
    public static SeoRules Bind(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var rules = new SeoRules();
        configuration.GetSection(SectionName).Bind(rules);
        rules.EnsureConsistent();

        return rules;
    }

    /// <exception cref="InvalidOperationException">A minimum exceeds its maximum, or a limit is not positive.</exception>
    public void EnsureConsistent()
    {
        var pairs = new (string Name, int Min, int Max)[]
        {
            (nameof(TitleMaxLength), TitleMinLength, TitleMaxLength),
            (nameof(MetaDescriptionMaxLength), MetaDescriptionMinLength, MetaDescriptionMaxLength),
            (nameof(KeyPhraseMaxCount), KeyPhraseMinCount, KeyPhraseMaxCount),
            (nameof(KeyPhraseMaxLength), KeyPhraseMinLength, KeyPhraseMaxLength),
        };

        foreach (var (name, min, max) in pairs)
        {
            if (min < 0 || max < 1 || min > max)
            {
                throw new InvalidOperationException($"{SectionName}:{name} must be positive and not below its minimum.");
            }
        }

        if (new[] { KeyPhraseMaxWords, AltTextMaxLength, AnchorTextMaxLength, MaxInternalLinks, SlugMaxLength }.Any(limit => limit < 1))
        {
            throw new InvalidOperationException($"{SectionName} limits must be positive.");
        }

        // Upper bounds too: a limit far past anything a search result shows, or past what the rest of the pipeline
        // carries, is a misconfiguration, not a preference. Link ideas cannot exceed the candidates offered.
        var ceilings = new (string Name, int Value, int Ceiling)[]
        {
            (nameof(TitleMaxLength), TitleMaxLength, 300),
            (nameof(MetaDescriptionMaxLength), MetaDescriptionMaxLength, 500),
            (nameof(KeyPhraseMaxCount), KeyPhraseMaxCount, 20),
            (nameof(KeyPhraseMaxLength), KeyPhraseMaxLength, 100),
            (nameof(KeyPhraseMaxWords), KeyPhraseMaxWords, 10),
            (nameof(AltTextMaxLength), AltTextMaxLength, 500),
            (nameof(AnchorTextMaxLength), AnchorTextMaxLength, 200),
            (nameof(MaxInternalLinks), MaxInternalLinks, 40),
            (nameof(SlugMaxLength), SlugMaxLength, 200),
        };

        foreach (var (name, value, ceiling) in ceilings)
        {
            if (value > ceiling)
            {
                throw new InvalidOperationException($"{SectionName}:{name} must not exceed {ceiling}.");
            }
        }
    }
}

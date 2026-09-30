using System.Text.RegularExpressions;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>One thing the SEO scanner found that the recipe, or the image, does not support.</summary>
public sealed record AiSeoFinding(AiSeoSection Section, int? ItemIndex, AiWarningKind Kind, string Code, string Message);

/// <summary>
/// Finds unsupported claims in generated SEO copy, in code (RCPUB-002). Findings are the server's warnings; nothing
/// the model emits can remove one.
/// </summary>
/// <remarks>
/// <para>
/// <strong>SEO output is recommendation, never measurement.</strong> This finds the measurements a model might
/// dress a recommendation in — search volume, ranking, difficulty, competitors, traffic — because there is no
/// connected source to back any of them (ai.md). It also runs the editorial scanner's checks over every text
/// (figures, safety and health claims, invented provenance), since a meta description is as public as a headnote.
/// </para>
/// <para>
/// <strong>Alt text is held to what was supplied, not what a picture might show.</strong> Nothing has analysed an
/// image's pixels. Any colour, texture, container or plating the image's own caption does not name is reported as an
/// unsupported visual detail, and every alt text carries a finding saying it was not checked against the image.
/// </para>
/// <para>This is a net, not a proof, for the reasons <see cref="AiEditorialClaimScanner"/> gives.</para>
/// </remarks>
public static partial class AiSeoClaimScanner
{
    public const string UnsupportedMetric = "seo.unsupported_metric";

    public const string UnsupportedVisualDetail = "seo.unsupported_visual_detail";

    public const string AltTextNotCheckedAgainstImage = "seo.alt_text_not_checked_against_image";

    public const string SlugUniquenessNotChecked = "seo.slug_uniqueness_not_checked";

    public const string KeyPhrasesNotFromSearchData = "seo.key_phrases_not_from_search_data";

    public const string LinkIdeasNotChecked = "seo.link_ideas_not_checked";

    // Matched against canonical text with hyphens read as spaces, so "search-volume" and "search volume" are one phrase.
    [GeneratedRegex(
        @"(?<!\w)(?:search(?:es)? volumes?|(?:high|low|big|huge)[ ]volume|(?:monthly|yearly|annual|daily|weekly) searches|searches (?:per|a|each) (?:day|week|month|year)|thousands? of (?:searches|visits|views|clicks|people)|(?:most|highly|frequently|often) searched|people search|popular (?:on|in|with) (?:google|bing|pinterest|search)|trending|viral|in demand|(?:high|low|big|huge|strong) (?:demand|competition|difficulty)|competition|competitive|keyword (?:difficulty|density|volume)|kds?|rank(?:ing|ings|s|ed|er)?|serps?|top[ ]\d+|top (?:result|spot|pick|position)|first (?:page|result)|page (?:one|1)|position (?:one|1)|no\.? ?1|#\s?1|number (?:one|1)|competitors?|outrank\w*|traffic|visitors?|page ?views?|click through|ctr|cpc|bounce rate|conversion rate|domain (?:authority|rating)|seo score|backlinks?|impressions|[0-9][0-9,.]*\s*[km]?\s+(?:searches|visits|visitors|views|clicks))(?!\w)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Metric();

    // How an image looks: colour, texture, cooking state, plating, container and scene, angle. Read with hyphens as spaces.
    [GeneratedRegex(
        @"\b(?:golden|brown|browned|red|green|yellow|orange|white|black|blue|purple|pink|silver|dark|light|bright|vibrant|colou?rful|crisp|crispy|crunchy|fluffy|glossy|glistening|creamy|moist|juicy|tender|flaky|gooey|steaming|steamy|steam|bubbly|bubbling|melting|oozing|caramelized|caramelised|charred|toasted|hot|warm|fresh|plated|garnished|drizzled|sprinkled|dusted|topped|stacked|layered|layers|slices|wedge|sprig|rustic|wooden|marble|ceramic|cast iron|skillet|pan|tray|mug|glass|fork|spoon|napkin|hand|hands|sunlight|shadow|background|close up|overhead|top down|side view|from above|sliced|cut open|on a plate|in a bowl|a bowl of|on a table|served (?:on|in))\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VisualDetail();

    /// <param name="captions">Each asset link's caption by id; absent or blank when the creator wrote none.</param>
    /// <param name="recipeTitle">The recipe's title. Not evidence of how an image looks, so it never licenses a visual word.</param>
    public static IReadOnlyList<AiSeoFinding> Scan(
        AiSeoSections sections,
        AiEditorialSourceFacts source,
        IReadOnlyDictionary<Guid, string?> captions,
        string recipeTitle)
    {
        ArgumentNullException.ThrowIfNull(sections);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(captions);

        var findings = new List<AiSeoFinding>();

        void Text(AiSeoSection section, int? index, string text)
        {
            foreach (var (kind, code, message) in AiEditorialClaimScanner.ScanText(text, source))
            {
                findings.Add(new AiSeoFinding(section, index, kind, code, message));
            }

            foreach (var metric in Metric().Matches(AiEditorialProse.Canonicalize(text).Replace('-', ' ')).Select(match => match.Value).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                findings.Add(new AiSeoFinding(
                    section, index, AiWarningKind.UnverifiedClaim, UnsupportedMetric,
                    $"\"{Excerpt(metric)}\" is a search metric or ranking claim. No connected source supports it, and none may be invented."));
            }
        }

        if (sections.SeoTitle is not null) Text(AiSeoSection.SeoTitle, null, sections.SeoTitle.Text);
        if (sections.MetaDescription is not null) Text(AiSeoSection.MetaDescription, null, sections.MetaDescription.Text);

        for (var i = 0; i < sections.KeyPhrases.Count; i++) Text(AiSeoSection.KeyPhrases, i, sections.KeyPhrases[i].Phrase);

        for (var i = 0; i < sections.InternalLinks.Count; i++)
        {
            Text(AiSeoSection.InternalLinks, i, $"{sections.InternalLinks[i].AnchorText} {sections.InternalLinks[i].Reason}");
        }

        for (var i = 0; i < sections.AltText.Count; i++)
        {
            var alt = sections.AltText[i];
            Text(AiSeoSection.AltText, i, alt.Text);

            // Only what the creator's caption for THIS image says may be repeated. The recipe's title is not
            // evidence of how a photograph looks: a dish called "Golden Milk" says nothing about its picture.
            var supplied = AiEditorialProse.Canonicalize(captions.GetValueOrDefault(alt.AssetLinkId) ?? string.Empty).Replace('-', ' ');

            foreach (var detail in VisualDetail().Matches(AiEditorialProse.Canonicalize(alt.Text).Replace('-', ' ')).Select(match => match.Value).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (Regex.IsMatch(supplied, $@"(?<![\w-]){Regex.Escape(detail)}(?![\w-])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                {
                    continue;
                }

                findings.Add(new AiSeoFinding(
                    AiSeoSection.AltText, i, AiWarningKind.UnverifiedClaim, UnsupportedVisualDetail,
                    $"\"{Excerpt(detail)}\" describes how the image looks, but only the image's caption was supplied; nothing has looked at the image."));
            }

            findings.Add(new AiSeoFinding(
                AiSeoSection.AltText, i, AiWarningKind.Limitation, AltTextNotCheckedAgainstImage,
                "This alt text was written from the caption or title and has not been checked against the image. Confirm it describes what is shown."));
        }

        // Standing findings, always present with their section: a reader must never mistake a recommendation for a measurement.
        if (sections.KeyPhrases.Count > 0)
        {
            findings.Add(new AiSeoFinding(
                AiSeoSection.KeyPhrases, null, AiWarningKind.Limitation, KeyPhrasesNotFromSearchData,
                "These phrases come from the recipe's own wording. No search data was consulted, so nothing here says they are searched for."));
        }

        if (sections.InternalLinks.Count > 0)
        {
            findings.Add(new AiSeoFinding(
                AiSeoSection.InternalLinks, null, AiWarningKind.Limitation, LinkIdeasNotChecked,
                "These are ideas for linking between your own recipes. No page exists to link to yet, and each target was approved when this was written, not when you read it."));
        }

        return findings;
    }

    private static string Excerpt(string value) => value.Length <= 60 ? value.Trim() : value[..60].Trim();
}

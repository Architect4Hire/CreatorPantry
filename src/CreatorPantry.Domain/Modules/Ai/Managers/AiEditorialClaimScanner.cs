using System.Text.RegularExpressions;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>One thing the scanner found in generated prose that the recipe does not support.</summary>
/// <param name="Section">The section it was found in.</param>
/// <param name="ItemIndex">The item within a list section, or null for a single-text section.</param>
public sealed record AiEditorialFinding(
    AiEditorialSection Section, int? ItemIndex, AiWarningKind Kind, string Code, string Message);

/// <summary>
/// Finds claims in generated editorial prose that the pinned recipe does not support, in code (RCPUB-001).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Findings are warnings, not rejections,</strong> and they are the server's: they are added to the
/// proposal after validation, and nothing the model emits can remove one. The creator sees each beside the
/// text it is about, and editing or accepting is their decision.
/// </para>
/// <para>
/// <strong>Four kinds</strong>, each a deterministic check rather than a judgement: a figure the recipe does not
/// state — checked in context, so a duration needs the same duration in the same unit, a temperature the same
/// temperature in the same scale, and a measured amount the same amount in the same unit; a safety, allergen, dietary or health claim the creator did not write; storage or reheating
/// guidance the recipe's own storage notes do not give; and an experience or provenance claim ("my
/// grandmother", "tested five times") the creator's own prose does not make.
/// </para>
/// <para>
/// Text is read in canonical form (<see cref="AiEditorialProse.Canonicalize"/>), so fullwidth digits, non-breaking
/// hyphens, zero-width characters and spelled-out numbers do not slip past a phrase list. Support is taken from
/// the creator's own prose, never from an ingredient line or a step, and never from a negation of the claim.
/// </para>
/// <para>
/// <strong>This is a net, not a proof.</strong> A phrase list misses paraphrase it has not heard of, and a
/// figure can be wrong while matching another figure. A clean scan means nothing in these four shapes was found;
/// it is never a statement that the prose is correct or safe, and nothing downstream may present it as one.
/// </para>
/// </remarks>
public static partial class AiEditorialClaimScanner
{
    public const string UnsupportedNumber = "editorial.unsupported_number";

    public const string UnsupportedSafetyClaim = "editorial.unsupported_safety_claim";

    public const string UnsupportedStorageGuidance = "editorial.unsupported_storage_guidance";

    public const string UnsupportedProvenance = "editorial.unsupported_provenance";

    /// <summary>The sentence a storage section uses, and only that, when the recipe gives no storage guidance to repeat.</summary>
    public const string NoStorageGuidance = "no storage guidance supplied";

    // A closed list, and a net rather than a proof (see the remarks). It covers four shapes: a dietary or allergen
    // label (including plurals, "suitable for vegans" and "no/without <allergen>"); a health or safety
    // assurance; method advice that touches food safety (raw eggs or batter, rinsing poultry, "safe to taste",
    // leaving food out); and a condition the recipe is presented as suitable for.
    [GeneratedRegex(
        @"\b(?:(?:gluten|dairy|nut|peanut|tree[- ]nut|egg|soy|lactose|sugar|wheat|sesame|shellfish|fish|allergen|allergy)[- ]free|gf|vegans?|vegetarians?|plant[- ]based|pescatarians?|keto|paleo|whole30|halal|kosher|celiacs?|coeliacs?|diabetics?|diabetic[- ]friendly|diabetes|safe (?:to eat|to serve|for)|allergy[- ]friendly|allergen[- ]friendly|allergen[- ]safe|low[- ](?:carb|calorie|fat|sodium|sugar|glycemic|gi)|glycemic|(?:suitable|appropriate|ideal|safe|okay|ok|fine) for (?:vegans?|vegetarians?|diabetics?|celiacs?|coeliacs?|kids|children|toddlers|babies|infants|pregnan\w*|people with|those with|anyone with)|(?:safe|okay|ok|fine) to (?:eat|serve|taste|sample|lick|consume)|(?:food|kid|toddler|baby|pregnancy)[- ]safe|perfectly safe|guarantee[ds]?|100% safe|kills? (?:bacteria|germs)|healthy|healthier|heart[- ]healthy|nutritious|anti[- ]inflammatory|boosts? (?:your )?immun\w+|good for (?:your )?(?:gut|digestion|heart)|superfood|detox\w*|cures?|cooked through|won't spoil|(?:no|without|free of|contains no|does not contain|doesn't contain) (?:added )?(?:gluten|dairy|nuts?|peanuts?|eggs?|soy|lactose|sugar|wheat|sesame|shellfish|fish|allergens?)|raw (?:eggs?|batter|dough|flour|chicken|poultry|meat|pork|beef|fish)|rinse (?:the |your )?(?:chicken|poultry|turkey|meat|pork|beef)|(?:can|may|will|could) (?:safely )?(?:stay|sit) out|medium[- ]rare|pregnan\w*|botulism|salmonella|home[- ]canning|canning|ferment\w*)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SafetyClaim();

    [GeneratedRegex(
        @"\b(?:refrigerat\w*|fridge|freez\w*|frozen|thaw\w*|reheat\w*|leftovers?|overnight|airtight|stor(?:e|es|ed|ing|age)(?!-bought)|keeps?\s+(?:for|up to|well|fresh|fine)|will (?:keep|last)|lasts?|shelf[- ]life|room temperature|spoil\w*|sit out|left out|cool completely|container)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StorageWord();

    [GeneratedRegex(
        @"\bmy (?:grandmother|grandma|grandpa|grandfather|mom|mother|dad|father|family|nonna|aunt|uncle|husband|wife|kids|friend)\b|\bour family\b|\b(?:i|we)(?:'ve| have)? (?:tested|tried|perfected|developed|tweaked|made this)\b|\btested \w+ times\b|\b(?:family|reader|crowd) (?:favou?rite|approved)\b|\bpassed down\b|\bhanded down\b|\b(?:chef|kid)[- ](?:tested|approved)\b|\btried[- ]and[- ]true\b|\baward[- ]winning\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Provenance();

    /// <summary>
    /// The same four checks over one piece of prose, for a capability whose sections are not the editorial ones
    /// (the SEO package). Findings carry no section: the caller knows where the text came from.
    /// </summary>
    public static IReadOnlyList<(AiWarningKind Kind, string Code, string Message)> ScanText(
        string text, AiEditorialSourceFacts source)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(source);

        var findings = new List<AiEditorialFinding>();
        var canonical = AiEditorialProse.Canonicalize(text);

        CheckFigures(findings, AiEditorialSection.Headnote, null, text, source);
        CheckSafety(findings, AiEditorialSection.Headnote, null, canonical, source);
        CheckStorage(findings, AiEditorialSection.Headnote, null, canonical, source);
        CheckProvenance(findings, AiEditorialSection.Headnote, null, canonical, source);

        return [.. findings.Select(finding => (finding.Kind, finding.Code, finding.Message))];
    }

    /// <summary>
    /// The safety, allergen, dietary and health check alone, over one piece of prose that has no recipe behind
    /// it at all (11A.24's style samples).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Why only this one of the four.</strong> A style sample is written about a short subject phrase,
    /// not about a recipe, so there is no source for a figure to agree with — and running
    /// <see cref="CheckFigures"/> against nothing would flag "one pan" and "a couple of lemons" on almost every
    /// sample, which is noise that would bury the finding that matters. The storage and provenance checks are
    /// about a recipe's own notes and have nothing to compare against either.
    /// </para>
    /// <para>
    /// This check needs no source: a sample may not assure anyone that anything is safe, allergen-free, healthy
    /// or suitable for a diet, whatever the creator's guidance asks for, so every match is a finding.
    /// </para>
    /// <para>
    /// <strong>Still a net, not a proof</strong> — see the type's own remarks. A clean scan means nothing in
    /// this one shape was found, and never that a sample is safe to publish.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<(AiWarningKind Kind, string Code, string Message)> ScanTextForSafetyClaims(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var findings = new List<AiEditorialFinding>();

        CheckSafety(
            findings,
            AiEditorialSection.Headnote,
            null,
            AiEditorialProse.Canonicalize(text),
            AiEditorialSourceFacts.Of([], prose: null, storageNotes: null));

        return [.. findings.Select(finding => (finding.Kind, finding.Code, finding.Message))];
    }

    public static IReadOnlyList<AiEditorialFinding> Scan(AiEditorialSections sections, AiEditorialSourceFacts source)
    {
        ArgumentNullException.ThrowIfNull(sections);
        ArgumentNullException.ThrowIfNull(source);

        var findings = new List<AiEditorialFinding>();

        foreach (var (section, index, raw) in Enumerate(sections))
        {
            var text = AiEditorialProse.Canonicalize(raw);

            CheckFigures(findings, section, index, raw, source);
            CheckSafety(findings, section, index, text, source);
            CheckStorage(findings, section, index, text, source);
            CheckProvenance(findings, section, index, text, source);
        }

        return findings;
    }

    private static IEnumerable<(AiEditorialSection Section, int? Index, string Text)> Enumerate(AiEditorialSections sections)
    {
        if (sections.Headnote is not null) yield return (AiEditorialSection.Headnote, null, sections.Headnote.Text);
        if (sections.Introduction is not null) yield return (AiEditorialSection.Introduction, null, sections.Introduction.Text);

        for (var i = 0; i < sections.Tips.Count; i++) yield return (AiEditorialSection.Tips, i, sections.Tips[i].Text);

        for (var i = 0; i < sections.Substitutions.Count; i++)
        {
            yield return (AiEditorialSection.Substitutions, i, $"{sections.Substitutions[i].Suggestion} {sections.Substitutions[i].CulinaryNote}");
        }

        if (sections.StorageReheating is not null) yield return (AiEditorialSection.StorageReheating, null, sections.StorageReheating.Text);

        for (var i = 0; i < sections.Faq.Count; i++) yield return (AiEditorialSection.Faq, i, $"{sections.Faq[i].Question} {sections.Faq[i].Answer}");

        if (sections.Cta is not null) yield return (AiEditorialSection.Cta, null, sections.Cta.Text);
    }

    private static void CheckFigures(
        List<AiEditorialFinding> findings, AiEditorialSection section, int? index, string raw, AiEditorialSourceFacts source)
    {
        // A storage section is held to the storage notes alone: a duration elsewhere in the recipe does not make
        // a storage duration supported.
        var storage = section is AiEditorialSection.StorageReheating;
        var times = storage ? source.StorageTimePairs : source.TimePairs;
        var temperatures = storage ? source.StorageTemperatures : source.Temperatures;
        var reported = new HashSet<string>(StringComparer.Ordinal);

        foreach (var token in AiEditorialProse.Tokens(raw))
        {
            var supported = token.Kind switch
            {
                AiEditorialTokenKind.Time => times.Contains(token.Pair),
                AiEditorialTokenKind.Temperature => AiEditorialProse.TemperatureSupported(temperatures, token.Number, token.Pair),
                AiEditorialTokenKind.Measure => source.Measures.Contains(token.Pair),
                _ => source.Numbers.Contains(token.Number),
            };

            if (supported || !reported.Add($"{token.Kind}:{token.Number}:{token.Pair}"))
            {
                continue;
            }

            var what = token.Kind switch
            {
                AiEditorialTokenKind.Time => "a duration the recipe does not state in that unit",
                AiEditorialTokenKind.Temperature => "a temperature the recipe does not state in that scale",
                AiEditorialTokenKind.Measure => "an amount the recipe does not state in that unit",
                _ => "a count, time, temperature or yield the recipe does not state",
            };

            findings.Add(new AiEditorialFinding(
                section, index, AiWarningKind.UnverifiedClaim, UnsupportedNumber,
                $"\"{Excerpt(token.Text)}\" is {what}."));
        }
    }

    private static void CheckSafety(
        List<AiEditorialFinding> findings, AiEditorialSection section, int? index, string text, AiEditorialSourceFacts source)
    {
        foreach (var phrase in SafetyClaim().Matches(text).Select(match => match.Value).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (SupportedByProse(source.Prose, phrase))
            {
                continue;
            }

            findings.Add(new AiEditorialFinding(
                section, index, AiWarningKind.SafetyCaution, UnsupportedSafetyClaim,
                $"\"{Excerpt(phrase)}\" is a safety, allergen, dietary or health claim the recipe's own notes do not make. "
                    + "Nothing here can verify it."));
        }
    }

    private static void CheckStorage(
        List<AiEditorialFinding> findings, AiEditorialSection section, int? index, string text, AiEditorialSourceFacts source)
    {
        var blank = string.IsNullOrWhiteSpace(source.StorageNotes);

        if (section is AiEditorialSection.StorageReheating
            && blank
            && !string.Equals(text.TrimEnd('.', ' ', '!'), NoStorageGuidance, StringComparison.OrdinalIgnoreCase))
        {
            // Exactly the sentinel, not a sentence that contains it: "Keeps 5 days. No storage guidance supplied."
            // is storage guidance with a disclaimer stapled on.
            findings.Add(new AiEditorialFinding(
                section, index, AiWarningKind.SafetyCaution, UnsupportedStorageGuidance,
                "The recipe gives no storage guidance, so this storage and reheating text is not grounded in it."));

            return;
        }

        if (blank && section is AiEditorialSection.StorageReheating)
        {
            return;
        }

        var stems = StorageWord().Matches(source.StorageNotes).Select(match => Stem(match.Value)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var word in StorageWord().Matches(text).Select(match => match.Value).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            // Supported only by the storage notes: a step that freezes the dough does not license advice to
            // freeze the bread.
            if (stems.Contains(Stem(word)))
            {
                continue;
            }

            findings.Add(new AiEditorialFinding(
                section, index, AiWarningKind.SafetyCaution, UnsupportedStorageGuidance,
                $"\"{Excerpt(word)}\" gives storage or reheating guidance the recipe's storage notes do not."));
        }
    }

    private static void CheckProvenance(
        List<AiEditorialFinding> findings, AiEditorialSection section, int? index, string text, AiEditorialSourceFacts source)
    {
        foreach (var phrase in Provenance().Matches(text).Select(match => match.Value).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (SupportedByProse(source.Prose, phrase))
            {
                continue;
            }

            findings.Add(new AiEditorialFinding(
                section, index, AiWarningKind.UnverifiedClaim, UnsupportedProvenance,
                $"\"{Excerpt(phrase)}\" claims an experience or origin the recipe does not record."));
        }
    }

    /// <summary>
    /// Whether the creator's own prose makes this claim: the whole phrase, not a fragment of a longer word, and
    /// not under a negation ("not vegan", "non-vegan", "never tested").
    /// </summary>
    private static bool SupportedByProse(string prose, string phrase) =>
        !string.IsNullOrEmpty(prose)
        && Regex.IsMatch(
            prose,
            $@"(?<![\w-])(?<!\b(?:non|not|no|never|isn't|aren't|wasn't)[\s-])(?<!\b(?:isn't|aren't|wasn't)\s){Regex.Escape(phrase)}(?![\w-])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string Stem(string word) =>
        word.Length <= 5 ? word.ToLowerInvariant() : word[..5].ToLowerInvariant();

    private static string Excerpt(string value) =>
        value.Length <= 60 ? value.Trim() : value[..60].Trim();
}

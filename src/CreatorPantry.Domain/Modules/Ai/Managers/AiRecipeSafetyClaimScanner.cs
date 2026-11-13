using System.Text.RegularExpressions;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// The assurances a recipe capability's free text may never carry: that something is free of an allergen, safe
/// to eat, suitable for a person, or guaranteed (14.7, audit blocker BL-2).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why a second scanner rather than the editorial one.</strong>
/// <see cref="AiEditorialClaimScanner.ScanTextForSafetyClaims"/> exists and is wired to editorial, SEO, brand
/// and long-form copy, where a claim of any kind is out of place — so its pattern matches "vegan",
/// "plant-based", "fermentation", "canning" and "medium-rare" as well. Those words are the *subject* of recipe
/// substitution, adaptation and review: an adaptation whose goal is "make it vegan" would be refused by every
/// answer it gave. Run against these capabilities that scanner would either be ignored or would break them,
/// which is how the gap this closes came about in the first place.
/// </para>
/// <para>
/// <strong>What this matches instead.</strong> Only the assurance, never the subject. "Replace the butter with
/// oat milk" and "this makes the recipe vegan as written" pass; "dairy-free", "safe for coeliacs" and
/// "guaranteed allergen-free" do not. The distinction is the one the domain already draws: a *consequence* is
/// stated through the structured allergen and dietary effects, with the caution the validators require beside
/// it; an *assurance* about a person or a product is a claim no record here supports, whatever the prose says.
/// </para>
/// <para>
/// <strong>A net, not a proof.</strong> A clean scan means nothing in these shapes was found — never that a
/// proposal is safe. It is the enforcement half of a rule the prompts already state; the prompts keep stating
/// it, because a model that is asked not to claim mostly does not.
/// </para>
/// </remarks>
public static partial class AiRecipeSafetyClaimScanner
{
    /// <summary>The reason an answer is refused for carrying one. Correctable: the model can say it another way.</summary>
    public const string ReasonCode = AiOutputReason.UnsupportedSafetyAssurance;

    /// <summary>
    /// Assurances about safety, absence, suitability or a guarantee.
    /// </summary>
    /// <remarks>
    /// Deliberately not here, because each is ordinary recipe language rather than an assurance: a diet's name
    /// on its own ("vegan", "keto", "halal"), a method ("canning", "fermentation", "curing"), a doneness
    /// ("medium-rare", "cooked through"), a nutrient direction ("low sodium"), and curing a pork belly — a
    /// "cure" is a method here far more often than it is a health claim, so only curing an *ailment* counts.
    /// The high-risk *method*
    /// warnings (B-37) are what read a recipe's own text for those, deterministically, and they are not this
    /// scanner's job.
    /// </remarks>
    [GeneratedRegex(
        @"\b(?:(?:gluten|dairy|milk|nut|peanut|tree[- ]?nut|egg|soy|lactose|wheat|sesame|shellfish|fish|allergen|allergy)[- ]?free|free[- ](?:of|from)\s+(?:all\s+)?(?:gluten|dairy|milk|nuts?|peanuts?|tree[- ]?nuts?|eggs?|soy|lactose|wheat|sesame|shellfish|fish|allergens?)|(?:contains?|has|with)\s+no\s+(?:gluten|dairy|milk|nuts?|peanuts?|tree[- ]?nuts?|eggs?|soy|lactose|wheat|sesame|shellfish|fish|allergens?)|(?:does\s+not|doesn'?t)\s+contain\s+(?:any\s+)?(?:gluten|dairy|milk|nuts?|peanuts?|tree[- ]?nuts?|eggs?|soy|lactose|wheat|sesame|shellfish|fish|allergens?))\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 250)]
    private static partial Regex AbsenceClaim();

    /// <summary>
    /// The assurances no field of any kind may carry: that eating it is safe, that it suits a named group of
    /// people, that something is guaranteed, or that it cures an ailment.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="AbsenceClaim"/> because the two are refused in different places. Naming an
    /// absence is legitimate in a warning — "this recipe cannot be made nut-free without losing the crumb" is
    /// the honest limitation the capability exists to give, and refusing it would push the model toward a
    /// vaguer answer. Assuring a person is never legitimate anywhere, whatever the sentence around it.
    /// </remarks>
    [GeneratedRegex(
        @"\b(?:allergen[- ](?:safe|friendly)|allergy[- ](?:safe|friendly)|hypo[- ]?allergenic|(?:perfectly|completely|entirely|totally|100%)\s+safe|food[- ]safe|safe\s+(?:to\s+(?:eat|serve|consume|taste)|for)|(?:suitable|appropriate|fine|okay|ok)\s+for\s+(?:coeliacs?|celiacs?|diabetics?|pregnan\w+|infants?|babies|toddlers|people\s+with|those\s+with|anyone\s+with)|medically\s+(?:safe|suitable|approved)|doctor[- ]approved|no\s+(?:health\s+)?risk|guarantee[ds]?|guaranteeing|kills?\s+(?:all\s+)?(?:bacteria|germs|pathogens)|(?:cures?|heals?|reverses?|prevents?)\s+(?:your\s+)?(?:colds?|flu|cancer|diabetes|disease|illness|infections?|inflammation|hangovers?|acne|insomnia|anxiety|depression))\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 250)]
    private static partial Regex HardAssurance();

    /// <summary>A label word, ending right where a free-from term begins: "mustard labelled gluten-free".</summary>
    [GeneratedRegex(
        @"\b(?:labell?ed|certified|marked|sold|marketed|packaged)(?:\s+as)?\s+$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 250)]
    private static partial Regex LabelBefore();

    /// <summary>A label word straight after a free-from term: "gluten-free-labelled oats", "dairy-free certified".</summary>
    [GeneratedRegex(
        @"^[- ](?:labell?ed|certified)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 250)]
    private static partial Regex LabelAfter();

    /// <summary>
    /// A statement about something being labelled, rather than a choice of what to buy: "this bread is certified" —
    /// unless it is a relative clause about a product, "a mustard that is labelled".
    /// </summary>
    [GeneratedRegex(
        @"(?<!\b(?:that|which)\s+)\b(?:is|are|was|were|be|been|being|it's|they're)\s+(?:(?:labell?ed|certified|marked|sold|marketed|packaged)(?:\s+as)?\s+)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 250)]
    private static partial Regex StatedOfTheDish();

    /// <summary>
    /// The first claim in <paramref name="text"/> that may not appear in creator-facing content, or null.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both tiers: an absence claim and an assurance. For a value that becomes part of a recipe, or prose a
    /// creator reads as a finding, an absence claim with nothing behind it is exactly the thing that must not
    /// arrive — "Gluten-Free Buttermilk Soda Bread" as a proposed title is the audit's blocker case.
    /// </para>
    /// <para>
    /// <strong>One allowance: a product's label.</strong> "Use a mustard labelled gluten-free" tells the creator
    /// what to buy, and the claim it repeats is the manufacturer's, on the packet, where a reader can check it — not
    /// an assurance CreatorPantry makes about the dish. Without it a dietary adaptation could not say the one useful
    /// thing it has to say, and live answers were refused for exactly this. The allowance is narrow: a label word
    /// must qualify the term ("labelled", "certified", "sold as", or "gluten-free-labelled"), and it never covers
    /// the dish itself ("this bread is certified gluten-free") or a title, which <see cref="FirstTitleAssurance"/>
    /// reads with no allowance at all. A hard assurance is refused whatever surrounds it.
    /// </para>
    /// </remarks>
    public static string? FirstAssurance(string? text) =>
        FirstMatch(text, absenceCounts: true, labelsAllowed: true);

    /// <summary>
    /// <see cref="FirstAssurance(string?)"/> with no allowance for a label: for a recipe's title, the name it is
    /// published under, where "Certified Gluten-Free Soda Bread" is the audit's blocker in different words.
    /// </summary>
    public static string? FirstTitleAssurance(string? text) =>
        FirstMatch(text, absenceCounts: true, labelsAllowed: false);

    /// <summary>The first claim across several pieces of text, in the order given.</summary>
    public static string? FirstAssurance(params string?[] texts)
    {
        ArgumentNullException.ThrowIfNull(texts);

        foreach (var text in texts)
        {
            if (FirstAssurance(text) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// The first assurance about safety, suitability or a guarantee, ignoring a named absence.
    /// </summary>
    /// <remarks>
    /// For a warning or a limitation, where naming what a recipe cannot be made is the answer rather than a
    /// claim. "It cannot be made nut-free" passes; "safe for people with a nut allergy" does not.
    /// </remarks>
    public static string? FirstHardAssurance(string? text) =>
        FirstMatch(text, absenceCounts: false, labelsAllowed: false);

    private static string? FirstMatch(string? text, bool absenceCounts, bool labelsAllowed)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (absenceCounts)
        {
            foreach (Match absence in AbsenceClaim().Matches(text))
            {
                if (labelsAllowed && NamesAProductLabel(text, absence))
                {
                    continue;
                }

                return absence.Value;
            }
        }

        return HardAssurance().Match(text) is { Success: true } hard ? hard.Value : null;
    }

    /// <summary>Whether this free-from term is a label on something to buy, not a statement about the dish.</summary>
    private static bool NamesAProductLabel(string text, Match absence)
    {
        var before = text[..absence.Index];
        var after = text[(absence.Index + absence.Length)..];

        var labelled = LabelBefore().IsMatch(before) || LabelAfter().IsMatch(after);
        return labelled && !StatedOfTheDish().IsMatch(before);
    }

    /// <summary>The message a refusal carries, quoting the phrase so the audit record says which words.</summary>
    public static string Message(string assurance, string where) =>
        $"{where} carries \"{Excerpt(assurance)}\", which assures safety, absence or suitability. Nothing in "
            + "CreatorPantry can verify that, so it may not be proposed; state the consequence as an allergen "
            + "or dietary effect with a safety caution instead. To tell the creator what to buy, name the "
            + "product's label rather than claiming it, as in \"mustard labelled gluten-free\" — never in a title.";

    private static string Excerpt(string phrase) =>
        phrase.Length <= 60 ? phrase : string.Concat(phrase.AsSpan(0, 57), "...");
}

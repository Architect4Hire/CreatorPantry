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

    /// <summary>
    /// The first claim in <paramref name="text"/> that may not appear in creator-facing content, or null.
    /// </summary>
    /// <remarks>
    /// Both tiers: an absence claim and an assurance. For a value that becomes part of a recipe, or prose a
    /// creator reads as a finding, an absence claim with nothing behind it is exactly the thing that must not
    /// arrive — "Gluten-Free Buttermilk Soda Bread" as a proposed title is the audit's blocker case.
    /// </remarks>
    public static string? FirstAssurance(string? text) =>
        FirstMatch(text, absenceCounts: true);

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
        FirstMatch(text, absenceCounts: false);

    private static string? FirstMatch(string? text, bool absenceCounts)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (absenceCounts && AbsenceClaim().Match(text) is { Success: true } absence)
        {
            return absence.Value;
        }

        return HardAssurance().Match(text) is { Success: true } hard ? hard.Value : null;
    }

    /// <summary>The message a refusal carries, quoting the phrase so the audit record says which words.</summary>
    public static string Message(string assurance, string where) =>
        $"{where} carries \"{Excerpt(assurance)}\", which assures safety, absence or suitability. Nothing in "
            + "CreatorPantry can verify that, so it may not be proposed; state the consequence as an allergen "
            + "or dietary effect with a safety caution instead.";

    private static string Excerpt(string phrase) =>
        phrase.Length <= 60 ? phrase : string.Concat(phrase.AsSpan(0, 57), "...");
}

using System.Text.RegularExpressions;

namespace CreatorPantry.Domain.Modules.Content.Managers;

/// <summary>
/// The rules of a content seed: what a token looks like, what the facets are called, and why nothing is weighted.
/// </summary>
public static partial class ContentSeedPolicy
{
    /// <summary>Eight bytes, which is eleven url-safe base64 characters.</summary>
    public const int TokenBytes = 8;

    /// <summary>
    /// Generous enough that a creator can paste a memorable token of their own — "spring-bakes" is a perfectly
    /// good seed — and bounded so it cannot become a payload.
    /// </summary>
    public const int TokenMaxLength = 64;

    [GeneratedRegex("^[A-Za-z0-9_-]+$")]
    private static partial Regex TokenPattern();

    /// <summary>Whether <paramref name="token"/> is a usable seed token. Url-safe characters only.</summary>
    public static bool IsToken(string token) => token.Length <= TokenMaxLength && TokenPattern().IsMatch(token);

    /// <summary>
    /// The longest pinned facet key accepted. Matches the widest key the catalogues hold — a channel key and a
    /// vocabulary code are both 64 — so nothing a catalogue could contain is refused on length.
    /// </summary>
    public const int KeyMaxLength = 64;

    /// <summary>
    /// The facet names mixed into the selection hash. Stable strings, because changing one re-points every token
    /// at a different answer for that facet.
    /// </summary>
    /// <remarks>
    /// Day has no entry: it is chosen from the seven days of the week by name, and a theme follows from the day
    /// rather than being selected on its own.
    /// </remarks>
    public static class Facets
    {
        public const string Cuisine = "cuisine";

        public const string DishType = "dish-type";

        public const string Method = "method";

        public const string PhotographyStyle = "photography-style";

        public const string Channel = "channel";

        public const string Day = "day";

        public const string Occasion = "occasion";
    }

    /// <summary>
    /// Why every facet is uniform over its active entries, recorded so the absence of weights is a decision.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nothing in the repository says one cuisine or occasion deserves to come up more often than another, and
    /// inventing per-entry weights would be exactly the fabricated editorial metric content.md forbids — a number
    /// that looks like evidence and is not. So selection is uniform, and
    /// <see cref="ContentSeedSelector"/> documents how real weights would be added if real data ever arrived.
    /// </para>
    /// <para>
    /// The one weighting that <em>is</em> grounded is the channel: a workspace that has declared its default
    /// channels on its brand profile has said, in its own data, where it publishes, so seeds draw from those and
    /// fall back to the whole catalogue for a workspace that has not. That is the creator's own answer being
    /// honoured rather than a preference being guessed.
    /// </para>
    /// <para>
    /// The weighting a creator would most feel — not repeating last week's seed — needs a record of what was
    /// generated before, and this endpoint persists nothing by design. It belongs to whatever later prompt gives
    /// seeds a history.
    /// </para>
    /// </remarks>
    public const string WeightingRationale =
        "Uniform over active entries, except that declared brand channels narrow the channel facet.";
}

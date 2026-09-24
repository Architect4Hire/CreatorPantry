namespace CreatorPantry.Domain.Modules.Ingredients.Managers;

/// <summary>
/// How a candidate's normalized text reached an <see cref="CreatorPantry.Domain.Modules.Ingredients.Data.Entities.Ingredient"/> —
/// its provenance, not a fuzzy score.
/// </summary>
/// <remarks>
/// There is no approximate-matching signal in this catalogue to justify a numeric confidence, so provenance
/// <em>is</em> the confidence: a canonical-name hit and an alias hit are both exact string matches, just
/// against different columns. The order these members are declared in is the precedence order
/// <see cref="IngredientMatcher"/> uses to break a tie between two different ingredients — do not reorder them
/// without updating it.
/// </remarks>
public enum IngredientMatchKind
{
    CanonicalName = 0,
    Alias = 1,
}

/// <summary>One row of the flattened lookup <see cref="IngredientMatcher"/> resolves candidates against.</summary>
/// <param name="NormalizedText">
/// Already normalized at rest — <c>Ingredient.NormalizedName</c> or <c>IngredientAlias.NormalizedAlias</c> —
/// so building the index costs no per-row normalization work.
/// </param>
public sealed record IngredientMatchIndexEntry(
    Guid IngredientId, string CanonicalName, string NormalizedText, IngredientMatchKind Kind);

/// <summary>One ingredient a candidate could resolve to.</summary>
public sealed record IngredientMatchCandidate(Guid IngredientId, string CanonicalName, IngredientMatchKind Kind);

/// <summary>The outcome of matching one free-form candidate string against the ingredient catalogue.</summary>
/// <remarks>
/// This never touches the text it was given, or anything that produced it — it is a new, separate result
/// layered alongside a tokenizer's output, not a mutation of it (recipes.md: a reference enriches a recipe
/// line, it never replaces the creator's entered text).
/// </remarks>
public sealed record IngredientMatchResult
{
    public required string InputText { get; init; }

    /// <summary>The confident match, or <see langword="null"/> when nothing resolved — including when it was ambiguous.</summary>
    public IngredientMatchCandidate? Resolved { get; init; }

    /// <summary>
    /// Other ingredients the same normalized text also touched, ranked below <see cref="Resolved"/> — or, when
    /// <see cref="IsAmbiguous"/> is true, every tied candidate with nothing chosen among them.
    /// </summary>
    public IReadOnlyList<IngredientMatchCandidate> Alternates { get; init; } = [];

    /// <summary>
    /// True when the candidate's normalized text matched more than one <em>different</em> ingredient at the
    /// same precedence tier, so nothing broke the tie. <see cref="Resolved"/> is <see langword="null"/> in this
    /// case — low confidence remains unresolved rather than guessed.
    /// </summary>
    public bool IsAmbiguous { get; init; }
}

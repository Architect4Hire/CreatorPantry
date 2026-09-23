using CreatorPantry.Domain.Managers.Reference;

namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// A create request reduced to what it actually means: text trimmed, absent choices defaulted, tags resolved
/// to their identities. Two requests with the same canonical form produce the same recipe.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This exists so one value serves two purposes that must never disagree.</strong> The facade hashes
/// it as the idempotency fingerprint, and Business maps the aggregate from it. Because both read the same
/// value, "same fingerprint" and "same recipe" cannot drift apart — and a field added to
/// <see cref="CreateRecipeViewModel"/> but forgotten here stops being mapped at all, which is a visible bug
/// rather than a silent gap in the fingerprint.
/// </para>
/// <para>
/// <strong>Why the fingerprint could not just be the request.</strong> Hashing the raw view model made three
/// pairs of identical requests look different: tags in another order, text with different padding, and an
/// omitted status against an explicit <see cref="RecipeStatus.Draft"/>. Each turned a legitimate retry into a
/// key-reuse conflict. The shared idempotency canonicalizer sorts object keys but deliberately preserves
/// array order — correct in general, since most arrays are ordered — so knowing that <em>this</em> request's
/// tags are a set is knowledge only this type has.
/// </para>
/// <para>
/// It carries no workspace, no actor and no timestamp. Those are the caller's identity and the clock's
/// business, and including them would make every request unique, which is the opposite of what a fingerprint
/// is for.
/// </para>
/// </remarks>
public sealed record CanonicalCreateRecipe
{
    public required string Title { get; init; }

    public string? Description { get; init; }

    public string? Headnote { get; init; }

    public string? Notes { get; init; }

    public string? StorageNotes { get; init; }

    public string? AttributionText { get; init; }

    public string? SourceUrl { get; init; }

    public Guid? CuisineId { get; init; }

    public Guid? CourseId { get; init; }

    public Guid? PrimaryTechniqueId { get; init; }

    public int? PrepTimeMinutes { get; init; }

    public int? CookTimeMinutes { get; init; }

    public int? RestTimeMinutes { get; init; }

    public int? TotalTimeMinutes { get; init; }

    public string? YieldText { get; init; }

    public decimal? YieldQuantity { get; init; }

    public Guid? YieldUnitId { get; init; }

    /// <summary>Resolved, never absent: an omitted status means <see cref="RecipeStatus.Draft"/>.</summary>
    public required RecipeStatus Status { get; init; }

    /// <summary>
    /// The tags to apply, deduplicated and ordered by normalized name.
    /// </summary>
    /// <remarks>
    /// Sorted rather than left in the creator's order, because a recipe's tags are a set —
    /// <c>RecipeTag</c> has no sort order, and the only thing the original order ever affected was which
    /// vocabulary row happened to be inserted first, each of which gets a random identifier anyway. Sorting
    /// is what makes two requests listing the same tags differently one request.
    /// </remarks>
    public required IReadOnlyList<RecipeTagName> Tags { get; init; }

    /// <summary>The recipe's method, in creator-defined order. Every group's and step's <c>Id</c> is null.</summary>
    public required IReadOnlyList<CanonicalInstructionGroup> Instructions { get; init; }

    /// <summary>Reduces a validated request to its meaning.</summary>
    /// <remarks>
    /// Pure and total. It assumes shape validation has already run — it does not reject anything, it only
    /// normalizes, so a request that never passed the validator canonicalizes just as happily and is simply
    /// wrong in the same way it was before.
    /// </remarks>
    public static CanonicalCreateRecipe From(CreateRecipeViewModel model) => new()
    {
        Title = Text(model.Title) ?? string.Empty,
        Description = Text(model.Description),
        Headnote = Text(model.Headnote),
        Notes = Text(model.Notes),
        StorageNotes = Text(model.StorageNotes),
        AttributionText = Text(model.AttributionText),
        SourceUrl = Text(model.SourceUrl),

        CuisineId = model.CuisineId,
        CourseId = model.CourseId,
        PrimaryTechniqueId = model.PrimaryTechniqueId,

        PrepTimeMinutes = model.PrepTimeMinutes,
        CookTimeMinutes = model.CookTimeMinutes,
        RestTimeMinutes = model.RestTimeMinutes,
        TotalTimeMinutes = model.TotalTimeMinutes,

        YieldText = Text(model.YieldText),
        YieldQuantity = model.YieldQuantity,
        YieldUnitId = model.YieldUnitId,

        // The one place a requested status becomes a domain state. Everything below this line — the
        // fingerprint, the aggregate, the version's readiness — speaks only RecipeStatus.
        Status = SettableRecipeStatus.ToDomain(model.Status),
        Tags = TagNames(model.Tags),
        Instructions = CanonicalInstructions.From(model.Instructions),
    };

    /// <summary>
    /// Trims, and collapses what is left of an empty field to <c>null</c>.
    /// </summary>
    /// <remarks>
    /// Trimming only. Internal spacing is the creator's — "1 cup   flour" is what they typed, and recipes.md
    /// makes that text canonical. Collapsing blank to null matters because otherwise "cleared" and "never
    /// set" become two states that look identical and compare differently.
    /// </remarks>
    private static string? Text(string? value)
    {
        var trimmed = value?.Trim();

        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static IReadOnlyList<RecipeTagName> TagNames(IReadOnlyList<string?>? tags)
    {
        if (tags is null)
        {
            return [];
        }

        var byIdentity = new Dictionary<string, RecipeTagName>(StringComparer.Ordinal);

        foreach (var tag in tags)
        {
            var name = Text(tag);
            if (name is null)
            {
                continue;
            }

            var normalized = NameNormalization.NormalizeName(name);

            // First wins, so the creator's own capitalisation of a tag they listed twice is the one that
            // would be stored. The validator already refuses that request; this only decides what happens
            // if something bypassed it.
            if (normalized.Length > 0)
            {
                byIdentity.TryAdd(normalized, new RecipeTagName(name, normalized));
            }
        }

        return [.. byIdentity.Values.OrderBy(tag => tag.NormalizedName, StringComparer.Ordinal)];
    }
}

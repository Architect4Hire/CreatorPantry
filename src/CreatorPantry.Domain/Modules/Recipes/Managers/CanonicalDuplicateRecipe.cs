namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// A duplicate request reduced to what it actually means: a trimmed title and the version to copy. Two
/// requests with the same canonical form ask for the same copy.
/// </summary>
/// <remarks>
/// The <see cref="CanonicalCreateRecipe"/> counterpart, and it exists for the same reason: the facade hashes
/// it as the idempotency fingerprint and Business reads the request from it, so "same fingerprint" and "same
/// copy" cannot drift apart. It carries no workspace, no actor and no timestamp — those would make every
/// request unique, which is the opposite of what a fingerprint is for.
/// </remarks>
public sealed record CanonicalDuplicateRecipe
{
    /// <summary>The copy's title, trimmed. Non-empty by validation.</summary>
    public required string Title { get; init; }

    /// <summary>
    /// The version to copy, or <c>null</c> for the source recipe's current version.
    /// </summary>
    /// <remarks>
    /// <c>null</c> survives canonicalization rather than being resolved to a number here. Resolving it would
    /// need a read, and this type is reached before one — but it also matters for the fingerprint: "copy
    /// whatever is current" and "copy version 4" are different requests even in the moment when they would
    /// produce the same recipe, because the first says the creator did not care which version it was.
    /// </remarks>
    public int? SourceVersionNumber { get; init; }

    /// <summary>Reduces a validated request to its meaning.</summary>
    public static CanonicalDuplicateRecipe From(DuplicateRecipeViewModel model) =>
        new()
        {
            // Non-null and non-blank by validation. Trimmed, but not otherwise touched: internal spacing is
            // the creator's, and recipes.md makes their text canonical.
            Title = (model.Title ?? string.Empty).Trim(),
            SourceVersionNumber = model.SourceVersionNumber,
        };

    /// <summary>
    /// What makes two duplicate requests the same one.
    /// </summary>
    /// <param name="recipeId">The source recipe, from the route.</param>
    /// <remarks>
    /// <para>
    /// The source recipe is part of it, or one key would cover copying every recipe the caller owns. The
    /// title is part of it because two copies under different names are two recipes the creator meant to
    /// have, and a replay that returned the first when the second was asked for would lose one of them.
    /// </para>
    /// <para>
    /// There is no concurrency token to include, unlike an edit or a restore — see
    /// <see cref="DuplicateRecipeViewModel"/>. That makes this fingerprint stable across time rather than
    /// across one state of the recipe, which is exactly right for a create: retrying the same copy hours
    /// later is still the same copy, and must not produce a second one.
    /// </para>
    /// </remarks>
    public object Fingerprint(Guid recipeId) => new
    {
        RecipeId = recipeId,
        SourceVersionNumber,
        Title,
    };
}

namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// A restore request reduced to what it actually means: a trimmed reason, with a blank one resolved to none.
/// Two requests with the same canonical form ask for the same restore.
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="CanonicalRecipePatch"/> counterpart, and it exists for the same reason: the facade hashes
/// it as the idempotency fingerprint and Business reads the restore from it, so "same fingerprint" and "same
/// restore" cannot drift apart.
/// </para>
/// <para>
/// It carries no workspace, no actor and no timestamp, for the reason
/// <see cref="CanonicalRecipePatch"/> records: those would make every request unique, which is the opposite
/// of what a fingerprint is for. It carries no content either — a restore has none to canonicalize.
/// </para>
/// </remarks>
public sealed record CanonicalRestoreRecipeVersion
{
    /// <summary>The state of the recipe this restore was composed against.</summary>
    public required string ExpectedConcurrencyToken { get; init; }

    /// <summary>Why the creator restored, or <c>null</c>.</summary>
    public string? Reason { get; init; }

    /// <summary>Reduces a validated request to its meaning.</summary>
    public static CanonicalRestoreRecipeVersion From(RestoreRecipeVersionViewModel model) =>
        new()
        {
            // Non-null by validation, which refuses an empty or malformed token before anything canonicalizes
            // it. Not trimmed: base64 has no insignificant whitespace, and a token with any is not one this
            // API issued — which the validator already said.
            ExpectedConcurrencyToken = model.ExpectedConcurrencyToken!,
            Reason = Text(model.Reason),
        };

    /// <summary>
    /// What makes two restore requests the same one.
    /// </summary>
    /// <param name="recipeId">The recipe from the route.</param>
    /// <param name="versionNumber">The version from the route.</param>
    /// <remarks>
    /// <para>
    /// Both route values are part of it. Without the recipe, one key would cover a restore of every recipe
    /// the caller owns; without the version number, "put it back to 3" and "put it back to 5" would replay as
    /// each other — the same key returning the wrong recipe, which is the failure an idempotency key exists
    /// to prevent rather than cause.
    /// </para>
    /// <para>
    /// <see cref="ExpectedConcurrencyToken"/> is in it, exactly as on an edit: a caller who re-read the recipe
    /// and re-sent the same key is asking for a different restore — one composed against a different state —
    /// and should be told the key was reused rather than handed the earlier answer.
    /// </para>
    /// <para>
    /// An anonymous object rather than a dictionary, unlike the patch's fingerprint: there are no absent
    /// fields here to distinguish from null ones, so a fixed shape says everything. The executor hashes it;
    /// nothing is stored.
    /// </para>
    /// </remarks>
    public object Fingerprint(Guid recipeId, int versionNumber) => new
    {
        RecipeId = recipeId,
        VersionNumber = versionNumber,
        ExpectedConcurrencyToken,
        Reason,
    };

    /// <inheritdoc cref="CanonicalRecipePatch" path="//remarks"/>
    private static string? Text(string? value)
    {
        var trimmed = value?.Trim();

        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}

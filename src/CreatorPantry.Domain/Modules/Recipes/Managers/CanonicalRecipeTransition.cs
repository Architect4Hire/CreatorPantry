namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// One requested transition, resolved out of its ViewModel: the target as a domain state, the reason as it will
/// be stored, and the token the request was composed against.
/// </summary>
/// <remarks>
/// <para>
/// Thin, because the body is three fields — and worth having anyway for the reason
/// <see cref="CanonicalCreateTestRun"/> gives: what gets hashed as the idempotency fingerprint and what
/// Business acts on are then one value rather than two that can drift. Hashing the raw body instead would make
/// "the same request" mean "the same bytes", so a client that re-sent the same transition with its fields in a
/// different order would be told its key had been reused.
/// </para>
/// <para>
/// The reason is trimmed here and nulled when blank, so <c>"  Too dense.  "</c> and <c>"Too dense."</c> are one
/// request and one stored sentence. Business trims again on the row it writes; the two agree because both read
/// this.
/// </para>
/// </remarks>
public sealed record CanonicalRecipeTransition(
    RecipeStatus Target,
    string? Reason,
    string? ExpectedConcurrencyToken)
{
    /// <summary>
    /// Resolves a validated request. The target is non-null by then — the validator refuses a body without
    /// one — and is dereferenced rather than defaulted, because there is no state a transition sensibly
    /// defaults to.
    /// </summary>
    public static CanonicalRecipeTransition From(RecipeReadinessTransitionViewModel model) =>
        new(
            RecipeTransitionTarget.ToDomain(model.TargetStatus!.Value),
            string.IsNullOrWhiteSpace(model.Reason) ? null : model.Reason.Trim(),
            model.ExpectedConcurrencyToken);

    /// <summary>What makes two requests the same transition.</summary>
    /// <param name="recipeId">The recipe, from the route.</param>
    /// <remarks>
    /// <para>
    /// The recipe is part of it, or one key would cover moving every recipe in the workspace. The token is part
    /// of it, following <see cref="CanonicalUpdateTestRun.Fingerprint"/>: a retry of the same request quotes
    /// the same token, and a caller who re-read the recipe and is now asking about a different state of it is
    /// asking something else.
    /// </para>
    /// <para>
    /// The reason is part of it because it is stored. Two requests that would write different history are not
    /// the same request, however alike the move looks.
    /// </para>
    /// </remarks>
    public object Fingerprint(Guid recipeId) => new
    {
        RecipeId = recipeId,
        Target,
        Reason,
        ExpectedConcurrencyToken,
    };
}

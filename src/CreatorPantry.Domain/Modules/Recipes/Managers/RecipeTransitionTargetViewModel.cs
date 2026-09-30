namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// The editorial states a readiness transition may aim at. Published as <c>RecipeTransitionTarget</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this is not <see cref="RecipeStatus"/>.</strong> One enum used for both directions publishes one
/// schema that the request and the response both reference, so the set a client may <em>send</em> and the set
/// it may <em>receive</em> become the same statement. They are not the same, and the reason is the same one
/// <see cref="SettableRecipeStatusViewModel"/> gives: a state reached by the system rather than asked for by a
/// request would silently widen the documented accepted input, and narrowing it afterwards is a breaking
/// change (api-contract.md).
/// </para>
/// <para>
/// <strong>It happens to carry every state today, and that is a coincidence rather than the rule.</strong>
/// Every one of the six is the target of at least one move in <see cref="RecipeStatusTransitions"/> — Draft
/// only out of the archive, but that counts. A state added later for the system to reach on its own would go
/// on <see cref="RecipeStatus"/> and not here.
/// </para>
/// <para>
/// <strong>This is not the per-move rule.</strong> A schema enumerates what the type permits; which of those a
/// particular recipe may be moved to depends on where it currently is, which no schema and no validator can
/// know. <see cref="RecipeStatusTransitions.Find"/> answers that, in Business, where a refusal can say what the
/// recipe could have gone to instead.
/// </para>
/// <para>
/// <strong>The numeric values mirror <see cref="RecipeStatus"/> deliberately.</strong> The serializer accepts
/// an enum written as a number as well as a name, so a client that sends <c>1</c> must mean
/// <see cref="RecipeStatus.Approved"/>. The mapping below is still written out member by member rather than
/// cast, so the two can never drift into agreeing by accident.
/// </para>
/// </remarks>
public enum RecipeTransitionTargetViewModel
{
    /// <inheritdoc cref="RecipeStatus.Draft"/>
    /// <remarks>Reachable only out of the archive; see <see cref="RecipePolicy.UnarchivedStatus"/>.</remarks>
    Draft = 0,

    /// <inheritdoc cref="RecipeStatus.Approved"/>
    Approved = 1,

    /// <inheritdoc cref="RecipeStatus.Archived"/>
    Archived = 2,

    /// <inheritdoc cref="RecipeStatus.InDevelopment"/>
    InDevelopment = 3,

    /// <inheritdoc cref="RecipeStatus.Testing"/>
    Testing = 4,

    /// <inheritdoc cref="RecipeStatus.ReadyForReview"/>
    ReadyForReview = 5,
}

/// <summary>Translates a requested transition target into the editorial state the domain records.</summary>
public static class RecipeTransitionTarget
{
    /// <summary>The domain state a requested target means.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The value is not a member. Unreachable through HTTP — the validator rejects an undefined value with a
    /// field error before anything maps it, and it is checked because a number the serializer accepted is the
    /// one way an undefined value can exist at all.
    /// </exception>
    public static RecipeStatus ToDomain(RecipeTransitionTargetViewModel target) => target switch
    {
        RecipeTransitionTargetViewModel.Draft => RecipeStatus.Draft,
        RecipeTransitionTargetViewModel.Approved => RecipeStatus.Approved,
        RecipeTransitionTargetViewModel.Archived => RecipeStatus.Archived,
        RecipeTransitionTargetViewModel.InDevelopment => RecipeStatus.InDevelopment,
        RecipeTransitionTargetViewModel.Testing => RecipeStatus.Testing,
        RecipeTransitionTargetViewModel.ReadyForReview => RecipeStatus.ReadyForReview,
        _ => throw new ArgumentOutOfRangeException(nameof(target), target, "That is not a transition target."),
    };
}

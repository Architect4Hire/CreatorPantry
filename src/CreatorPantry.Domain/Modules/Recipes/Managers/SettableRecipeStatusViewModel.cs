namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// The editorial states a creator may ask a write to put a recipe in. Published as
/// <c>SettableRecipeStatus</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this is not <see cref="RecipeStatus"/>.</strong> One enum used for both directions publishes
/// one schema that both the request and the response reference, so the set a client may <em>send</em> and the
/// set it may <em>receive</em> become the same statement. They are not the same. <c>content.md</c> describes an
/// editorial workflow that grows — review, approved, scheduled, published — and states that arrive that way
/// are reached by the system, not asked for by a request body. Adding one to <see cref="RecipeStatus"/> for a
/// read would silently widen the documented accepted input, and narrowing it afterwards is a breaking change
/// (api-contract.md). Splitting the two makes a read-only state additive on the output enum and invisible
/// here, which is the whole point.
/// </para>
/// <para>
/// <strong>Growing this enum is safe; shrinking it is not.</strong> Widening what a request may carry is
/// compatible, so a later write seam that needs another state adds it here. Removing a member narrows
/// accepted values and breaks clients.
/// </para>
/// <para>
/// <strong>The numeric values mirror <see cref="RecipeStatus"/> deliberately.</strong> The serializer accepts
/// an enum written as a number as well as a name, so a client that already sends <c>1</c> for
/// <see cref="Approved"/> must keep meaning <see cref="RecipeStatus.Approved"/>. Renumbering here would silently
/// change what such a request asks for. The mapping is still written out member by member rather than cast,
/// so the two can never drift into agreeing by accident.
/// </para>
/// <para>
/// <strong>This is not the per-operation rule.</strong> A schema enumerates what the type permits; which of
/// those values a particular route accepts is a validator's business, because that is where a refusal can name
/// the field and say why. <see cref="Archived"/> is the original example: it belongs here, because archiving is
/// a thing a creator does, and <c>CreateRecipeViewModelValidator</c> still refuses it on a create with a
/// sentence that says so. Leaving it out of the enum instead would turn that clear field error into a generic
/// unreadable-body 400.
/// </para>
/// <para>
/// <strong>Since TESTRUN-005, <see cref="Draft"/> is the only value any route accepts.</strong> Editorial
/// state is a machine now (<see cref="RecipeStatusTransitions"/>), and a create or an edit that could set
/// <see cref="Approved"/> would be a second road to the terminal state with no readiness gate on it and no
/// transition recorded — precisely what the machine exists to prevent. The other two members stay in the
/// schema for the reason the paragraph above gives: a client that sends one gets a field error naming the
/// field and pointing at the transition route, which is more use than a 400 about an unreadable body. Both are
/// refused by the create and update validators.
/// </para>
/// </remarks>
public enum SettableRecipeStatusViewModel
{
    /// <inheritdoc cref="RecipeStatus.Draft"/>
    Draft = 0,

    /// <inheritdoc cref="RecipeStatus.Approved"/>
    /// <remarks>Refused on every write. Reached by transitioning to it; see the type's remarks.</remarks>
    Approved = 1,

    /// <inheritdoc cref="RecipeStatus.Archived"/>
    /// <remarks>Refused on every write, as it always has been on a create. See the type's remarks.</remarks>
    Archived = 2,
}

/// <summary>Translates a requested status into the editorial state the domain records.</summary>
public static class SettableRecipeStatus
{
    /// <summary>
    /// The domain state a requested status means, or <see cref="RecipeStatus.Draft"/> when none was requested.
    /// </summary>
    /// <remarks>
    /// An omitted status means <see cref="RecipeStatus.Draft"/>: a recipe that does not say where it is in the
    /// workflow is being written. Resolving it here rather than leaving it null keeps one answer to "what did
    /// this request ask for", which is what the idempotency fingerprint depends on.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The value is not a member. Unreachable through HTTP — the validator rejects an undefined value with a
    /// field error before anything maps it, and it is checked because a number the serializer accepted is the
    /// one way an undefined value can exist at all.
    /// </exception>
    public static RecipeStatus ToDomain(SettableRecipeStatusViewModel? requested) => requested switch
    {
        null => RecipeStatus.Draft,
        SettableRecipeStatusViewModel.Draft => RecipeStatus.Draft,
        SettableRecipeStatusViewModel.Approved => RecipeStatus.Approved,
        SettableRecipeStatusViewModel.Archived => RecipeStatus.Archived,
        _ => throw new ArgumentOutOfRangeException(nameof(requested), requested, "That is not a recipe status."),
    };
}

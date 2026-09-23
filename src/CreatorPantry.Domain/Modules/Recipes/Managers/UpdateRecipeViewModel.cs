using CreatorPantry.Domain.Managers.Patching;

namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// The body of <c>PATCH /api/v1/workspaces/{workspaceSlug}/recipes/{recipeId}</c>: a creator changing part
/// of a recipe they already have.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Submitted-field semantics.</strong> A field the body does not mention is left exactly as it is.
/// A field mentioned with a value is set to it. A field mentioned as <c>null</c> is cleared. The three are
/// distinguishable because every content field is a <see cref="PatchField{T}"/> — see that type for how the
/// serializer tells absence from <c>null</c>, and why a nullable property alone cannot. Without the
/// distinction a client that omits a field it has never heard of silently blanks it, which for a recipe
/// means deleting the creator's own words.
/// </para>
/// <para>
/// <strong>Tags replace, they do not merge.</strong> A submitted list becomes the recipe's complete set of
/// tags; an empty list and an explicit <c>null</c> both clear every tag. Merging would leave no way to
/// remove one, and a separate add/remove pair would be two ways to say one thing.
/// </para>
/// <para>
/// <strong>What is deliberately absent.</strong> No <c>WorkspaceId</c>, owner, author, version number,
/// timestamp or <c>YieldUnitDimension</c> — for the reasons <see cref="CreateRecipeViewModel"/> records at
/// length. Also no id: the recipe is named by the route. And no ingredients, equipment or asset links;
/// editing those means reordering and identifying children, which is a contract of its own —
/// <see cref="Instructions"/> is that contract, written for the method specifically.
/// </para>
/// <para>
/// <strong>Every field is nullable inside its <see cref="PatchField{T}"/>, including
/// <see cref="Title"/>.</strong> The wire can send <c>null</c> for anything, so the type says what the wire
/// can carry and the validator says what is allowed — which is where a refusal can name the field and
/// explain itself. Clearing a title is refused there, not made unrepresentable here.
/// </para>
/// </remarks>
public sealed record UpdateRecipeViewModel
{
    /// <summary>
    /// The <c>concurrencyToken</c> from the recipe this edit was composed against. Required.
    /// </summary>
    /// <remarks>
    /// Round-tripped from the read, which publishes it as
    /// <see cref="RecipeDetailServiceModel.ConcurrencyToken"/>. It is what makes a second creator's edit a
    /// recoverable conflict instead of a silent overwrite of the first one's work, so there is no default
    /// and no way to opt out: a write with nothing to check against is last-write-wins by another name.
    /// Opaque — a client stores it and sends it back, and never parses or compares it.
    /// </remarks>
    public string? ExpectedConcurrencyToken { get; init; }

    /// <summary>
    /// Why this edit was made, in the creator's own words. Optional, and recorded on the version this edit
    /// writes.
    /// </summary>
    /// <remarks>
    /// Not a <see cref="PatchField{T}"/>, because it is not a field of the recipe: it describes the change
    /// rather than the content, so there is nothing to leave alone or clear. A reason on its own is not a
    /// change either — a body carrying only a reason writes no version, because an explanation of nothing
    /// is not history.
    /// </remarks>
    public string? Reason { get; init; }

    /// <summary>The working title. May be changed, never cleared — a recipe without one has no name.</summary>
    public PatchField<string?> Title { get; init; }

    /// <summary>A short summary for listings and cards.</summary>
    public PatchField<string?> Description { get; init; }

    /// <summary>The editorial introduction that runs above the recipe.</summary>
    public PatchField<string?> Headnote { get; init; }

    /// <summary>The creator's own working notes.</summary>
    public PatchField<string?> Notes { get; init; }

    /// <inheritdoc cref="CreateRecipeViewModel.StorageNotes"/>
    public PatchField<string?> StorageNotes { get; init; }

    /// <summary>"Adapted from…", in the creator's words.</summary>
    public PatchField<string?> AttributionText { get; init; }

    /// <summary>Where the recipe came from, when there is a link.</summary>
    public PatchField<string?> SourceUrl { get; init; }

    /// <summary>The shared cuisine vocabulary.</summary>
    public PatchField<Guid?> CuisineId { get; init; }

    /// <summary>The shared course vocabulary — the role this plays in a meal.</summary>
    public PatchField<Guid?> CourseId { get; init; }

    /// <summary>The shared technique vocabulary: what the requirements call the recipe's method.</summary>
    public PatchField<Guid?> PrimaryTechniqueId { get; init; }

    public PatchField<int?> PrepTimeMinutes { get; init; }

    public PatchField<int?> CookTimeMinutes { get; init; }

    public PatchField<int?> RestTimeMinutes { get; init; }

    /// <inheritdoc cref="CreateRecipeViewModel.TotalTimeMinutes"/>
    public PatchField<int?> TotalTimeMinutes { get; init; }

    /// <summary>The yield as the creator phrased it. The canonical form.</summary>
    public PatchField<string?> YieldText { get; init; }

    /// <summary>The numeric yield, when there is one.</summary>
    public PatchField<decimal?> YieldQuantity { get; init; }

    /// <summary>
    /// The serving unit. Meaningless without a quantity — and because a patch names only part of the
    /// recipe, that pairing is checked against what the recipe <em>would become</em> rather than against
    /// this request alone.
    /// </summary>
    public PatchField<Guid?> YieldUnitId { get; init; }

    /// <summary>
    /// The recipe's complete set of tag names, in the creator's own words. Submitting replaces; omitting
    /// leaves them alone; <c>null</c> or <c>[]</c> clears them.
    /// </summary>
    public PatchField<IReadOnlyList<string?>?> Tags { get; init; }

    /// <summary>
    /// The editorial state. May be changed, never cleared — a recipe is always somewhere in the workflow.
    /// </summary>
    /// <remarks>
    /// <see cref="SettableRecipeStatusViewModel.Archived"/> is accepted here, unlike on a create: archiving
    /// is a state a recipe is moved to, and this is the route that moves it.
    /// </remarks>
    public PatchField<SettableRecipeStatusViewModel?> Status { get; init; }

    /// <summary>
    /// The recipe's complete method, in creator-defined order. Submitting replaces: a group or step named by
    /// an <c>id</c> is updated in place, one submitted without an <c>id</c> is new, and one the recipe
    /// currently has but this list does not name is removed. Omitting the field leaves the method exactly as
    /// it is; an empty list clears it to no steps at all.
    /// </summary>
    public PatchField<IReadOnlyList<RecipeInstructionGroupInputViewModel?>?> Instructions { get; init; }
}

namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// The body of <c>POST /api/v1/workspaces/{workspaceSlug}/recipes</c>: a creator starting a recipe.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What is deliberately absent.</strong> No <c>WorkspaceId</c> — it comes from the route and the
/// caller's membership, never from a body (tenancy.md). No author: the actor is
/// <c>IWorkspaceContext.MembershipId</c>. No version number, no timestamps, no row version, no provider
/// field. And no <c>YieldUnitDimension</c>: that column exists only so a composite foreign key can pin a
/// unit's dimension, and a client able to send it could contradict the unit it names.
/// </para>
/// <para>
/// <strong>Every property is nullable, including <see cref="Title"/>.</strong> The wire can send <c>null</c>
/// for anything, and a non-nullable property would hand the error message to the model binder instead of the
/// validator — where it would arrive without a stable error code. The existing workspace validator already
/// distrusts binding this way; this states it in the type.
/// </para>
/// <para>
/// This creates the recipe, not most of its contents. Ingredients and equipment are edited through their own
/// contract, which has ordering and concurrency concerns this one does not. Instructions are the exception,
/// present here and on <see cref="UpdateRecipeViewModel"/> alike, because a creator routinely writes the
/// method in the same sitting as the title.
/// </para>
/// </remarks>
public sealed record CreateRecipeViewModel
{
    /// <summary>The working title. The only required field — everything else can come later.</summary>
    public string? Title { get; init; }

    /// <summary>A short summary for listings and cards.</summary>
    public string? Description { get; init; }

    /// <summary>The editorial introduction that runs above the recipe.</summary>
    public string? Headnote { get; init; }

    /// <summary>The creator's own working notes.</summary>
    public string? Notes { get; init; }

    /// <summary>
    /// How to keep it, in the creator's words. Descriptive metadata, never a preservation or food-safety
    /// guarantee — recipes.md requires a vetted reference or an explicit caution for any claim of that kind,
    /// and free text here is neither.
    /// </summary>
    public string? StorageNotes { get; init; }

    /// <summary>"Adapted from…", in the creator's words.</summary>
    public string? AttributionText { get; init; }

    /// <summary>Where the recipe came from, when there is a link.</summary>
    public string? SourceUrl { get; init; }

    /// <summary>The shared cuisine vocabulary. Optional and additive, like every reference here.</summary>
    public Guid? CuisineId { get; init; }

    /// <summary>The shared course vocabulary — the role this plays in a meal.</summary>
    public Guid? CourseId { get; init; }

    /// <summary>The shared technique vocabulary: what the requirements call the recipe's method.</summary>
    public Guid? PrimaryTechniqueId { get; init; }

    public int? PrepTimeMinutes { get; init; }

    public int? CookTimeMinutes { get; init; }

    public int? RestTimeMinutes { get; init; }

    /// <summary>
    /// Accepted independently of the three above, and never derived from them. Prep overlaps cooking and
    /// resting is often unattended, so a creator who writes "about 2 hours, mostly waiting" means it.
    /// </summary>
    public int? TotalTimeMinutes { get; init; }

    /// <summary>The yield as the creator phrased it: "makes 12 muffins". The canonical form.</summary>
    public string? YieldText { get; init; }

    /// <summary>The numeric yield, when there is one. Additive to <see cref="YieldText"/>.</summary>
    public decimal? YieldQuantity { get; init; }

    /// <summary>The serving unit. Meaningless without <see cref="YieldQuantity"/>, and rejected without it.</summary>
    public Guid? YieldUnitId { get; init; }

    /// <summary>
    /// The creator's tag names, in their own words. Names rather than ids, so tagging is one action: a name
    /// that is new to the workspace becomes a new tag, and one that matches an existing tag after
    /// normalization reuses it rather than creating a near-duplicate.
    /// </summary>
    public IReadOnlyList<string?>? Tags { get; init; }

    /// <summary>
    /// The starting editorial state. Defaults to <see cref="RecipeStatus.Draft"/> when omitted, and never
    /// implies anything about external publication.
    /// </summary>
    /// <remarks>
    /// Typed as <see cref="SettableRecipeStatusViewModel"/> rather than <see cref="RecipeStatus"/> so that the
    /// states a request may carry and the states a response may report are two schemas that can evolve apart.
    /// <see cref="SettableRecipeStatusViewModel.Archived"/> is in the type but refused on a create — that rule
    /// is the validator's, where the refusal can name this field.
    /// </remarks>
    public SettableRecipeStatusViewModel? Status { get; init; }

    /// <summary>
    /// The recipe's method, in creator-defined order. A group's or step's <c>id</c> must be omitted here —
    /// nothing exists yet to name — and the validator refuses one.
    /// </summary>
    public IReadOnlyList<RecipeInstructionGroupInputViewModel?>? Instructions { get; init; }
}

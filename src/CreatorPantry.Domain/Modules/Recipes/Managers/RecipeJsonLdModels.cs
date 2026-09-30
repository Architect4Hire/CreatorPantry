namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// Everything <see cref="RecipeJsonLdGenerator"/> may read. The generator does no I/O, so a caller resolves
/// and supplies every fact; anything not supplied is omitted from the output rather than inferred.
/// </summary>
public sealed record RecipeJsonLdInput
{
    /// <summary>The exact pinned version being described.</summary>
    public required RecipeSnapshotDocument Snapshot { get; init; }

    /// <summary>The version's readiness. A <see cref="RecipeVersionReadiness.Ready"/> version may be described even if the recipe's status has moved on.</summary>
    public RecipeVersionReadiness Readiness { get; init; }

    /// <summary>The resolved name of <c>CuisineId</c>, or <c>null</c> when unset or unresolved.</summary>
    public string? CuisineName { get; init; }

    /// <summary>The resolved name of <c>CourseId</c>, or <c>null</c> when unset or unresolved.</summary>
    public string? CourseName { get; init; }

    /// <summary>
    /// An absolute http(s) URL the caller has already authorized for the hero image. The generator never
    /// derives one from a media asset id, so no storage path can leak through it.
    /// </summary>
    public string? ImageUrl { get; init; }

    /// <summary>The creator-accepted SEO revision for this version, or <c>null</c> when there is none.</summary>
    public RecipeJsonLdEditorial? Editorial { get; init; }
}

/// <summary>Accepted SEO recommendations the structured data may carry.</summary>
/// <param name="MetaDescription">The accepted meta description, or <c>null</c>.</param>
/// <param name="KeyPhrases">The accepted key phrases.</param>
/// <param name="IsCurrent">
/// Whether the revision's pins still match the source version. A stale revision is ignored, not used with a
/// caveat.
/// </param>
public sealed record RecipeJsonLdEditorial(
    string? MetaDescription,
    IReadOnlyList<string> KeyPhrases,
    bool IsCurrent);

/// <summary>One finding about the JSON-LD a recipe can or cannot produce.</summary>
/// <param name="Code">A stable machine-readable code from <see cref="RecipeJsonLdCodes"/>.</param>
/// <param name="Message">A sentence a creator can act on.</param>
public sealed record RecipeJsonLdIssue(string Code, string Message);

/// <summary>
/// The outcome of a generation. <see cref="Json"/> is present only when nothing required is missing, so a
/// caller can never publish structured data that is known to be incomplete.
/// </summary>
/// <param name="Json">The JSON-LD document, or <c>null</c> when <see cref="MissingRequired"/> is not empty.</param>
/// <param name="MissingRequired">Facts the structured data cannot be published without.</param>
/// <param name="Warnings">Facts that were omitted but do not block.</param>
public sealed record RecipeJsonLdReport(
    string? Json,
    IReadOnlyList<RecipeJsonLdIssue> MissingRequired,
    IReadOnlyList<RecipeJsonLdIssue> Warnings)
{
    public bool IsComplete => Json is not null && MissingRequired.Count == 0;
}

/// <summary>Stable codes for <see cref="RecipeJsonLdIssue"/>.</summary>
public static class RecipeJsonLdCodes
{
    public const string RecipeNotApproved = "recipe_json_ld_recipe_not_approved";
    public const string NameMissing = "recipe_json_ld_name_missing";
    public const string ImageMissing = "recipe_json_ld_image_missing";
    public const string ImageInvalid = "recipe_json_ld_image_invalid";
    public const string IngredientsMissing = "recipe_json_ld_ingredients_missing";
    public const string InstructionsMissing = "recipe_json_ld_instructions_missing";
    public const string TimesMissing = "recipe_json_ld_times_missing";
    public const string YieldMissing = "recipe_json_ld_yield_missing";
    public const string EditorialNotCurrent = "recipe_json_ld_editorial_not_current";
    public const string CuisineUnresolved = "recipe_json_ld_cuisine_unresolved";
    public const string CourseUnresolved = "recipe_json_ld_course_unresolved";
}

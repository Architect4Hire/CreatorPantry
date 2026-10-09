namespace CreatorPantry.Domain.Modules.Content.Managers;

/// <summary>
/// What a caller asks a seed for. Every field is optional: with none of them the server mints a token and picks
/// every facet.
/// </summary>
/// <remarks>
/// <para>
/// A pinned facet is a value the creator has already decided, given as the facet's stable key — the rest are then
/// selected around it. A key that no catalogue has, or one that names a retired entry, is refused rather than
/// ignored: a caller who pinned Thai and silently got Korean would have no way to tell.
/// </para>
/// <para>
/// Carries no workspace: that comes from the route and the caller's membership.
/// </para>
/// </remarks>
public sealed record ContentSeedQueryViewModel
{
    /// <summary>
    /// The token to generate from. Supplying the token a previous seed returned reproduces that seed.
    /// </summary>
    public string? Token { get; init; }

    /// <summary>A <c>Cuisine.Code</c>, such as <c>thai</c>.</summary>
    public string? Cuisine { get; init; }

    /// <summary>A <c>Course.Code</c>, such as <c>main-course</c>. The requirements call this the dish type.</summary>
    public string? DishType { get; init; }

    /// <summary>A <c>CookingTechnique.Code</c>, such as <c>stir-fry</c>.</summary>
    public string? Method { get; init; }

    /// <summary>A <c>PhotographyStyle.Key</c>, such as <c>overhead-flat-lay</c>.</summary>
    public string? PhotographyStyle { get; init; }

    /// <summary>A <c>ContentChannel.Key</c>, such as <c>instagram</c>.</summary>
    public string? Channel { get; init; }

    /// <summary>A day of the week. The workspace's theme for that day follows from it, when it has one.</summary>
    public DayOfWeek? Day { get; init; }

    /// <summary>An <c>Occasion.Key</c>, such as <c>weeknight</c>.</summary>
    public string? Occasion { get; init; }

    /// <summary>
    /// A recipe of this workspace to build the idea around. Its cuisine, course and primary technique become the
    /// seed's cuisine, dish type and method, and its title is named in the description.
    /// </summary>
    /// <remarks>
    /// Read through the recipe module's facade in the resolved workspace, so a recipe that does not exist and one
    /// that belongs to another workspace are refused alike.
    /// </remarks>
    public Guid? RecipeId { get; init; }

    /// <summary>
    /// The version of <see cref="RecipeId"/> to read, when the caller pinned one. Absent reads the recipe as it
    /// currently stands. Refused without a <see cref="RecipeId"/>.
    /// </summary>
    public Guid? RecipeVersionId { get; init; }
}

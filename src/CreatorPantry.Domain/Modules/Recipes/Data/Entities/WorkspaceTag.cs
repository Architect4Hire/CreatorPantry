using CreatorPantry.Domain.Managers.Persistence;

namespace CreatorPantry.Domain.Modules.Recipes.Data.Entities;

/// <summary>
/// One tag in a creator's own vocabulary — "weeknight", "freezer-friendly", "mum's". Workspace-owned:
/// private to one workspace, defined entirely by the creator, and never shared with another (tenancy.md).
/// </summary>
/// <remarks>
/// <para>
/// <strong>A vocabulary, not a string on a recipe.</strong> The alternative — free text repeated on every
/// recipe — cannot rename a tag without a mass update over creator content, cannot offer autocomplete
/// without scanning, and lets "Weeknight", "weeknight" and "week night" become three tags that look like one.
/// This is also what the shared <c>Cuisine</c> and <c>Course</c> vocabularies already promise: both decline
/// to model a recipe that belongs to two of them, on the stated grounds that the creator's own workspace tags
/// are where that is expressed.
/// </para>
/// <para>
/// <strong>An aggregate root</strong>, like <see cref="Recipe"/> and <see cref="RecipeVersion"/>: it has its
/// own lifetime, is listed and edited on its own, and outlives any one recipe that uses it.
/// </para>
/// <para>
/// <strong>Retired, not deleted</strong>, following every reference vocabulary in the platform. A tag still
/// attached to recipes cannot be removed — <see cref="RecipeTag"/>'s foreign key refuses it — so taking a tag
/// out of circulation means clearing <see cref="IsActive"/>, which leaves existing recipes intact and simply
/// drops it from the pickers.
/// </para>
/// <para>
/// <strong>Where this lives.</strong> It is in the recipes module because recipes are the only thing that
/// tags anything today. If content projects or media adopt tagging, this belongs in a module of its own —
/// that move is a namespace change and a table rename, not a redesign, and the model here does not have to
/// change to make it.
/// </para>
/// </remarks>
public class WorkspaceTag : IWorkspaceOwned
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    /// <summary>The creator's own capitalisation and spacing: "Freezer Friendly". What gets displayed.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The natural key within a workspace, from <see cref="CreatorPantry.Domain.Managers.Reference.NameNormalization.NormalizeName"/>.
    /// It is what makes "Freezer Friendly", "freezer friendly" and "FREEZER FRIENDLY" one tag rather than
    /// three, while <see cref="Name"/> keeps whichever form the creator typed first.
    /// </summary>
    public string NormalizedName { get; set; } = string.Empty;

    /// <summary>
    /// Whether the tag is offered for new input. A retired tag stays readable so recipes already carrying it
    /// keep resolving; it simply leaves the pickers.
    /// </summary>
    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }
}

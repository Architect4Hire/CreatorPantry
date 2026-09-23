using CreatorPantry.Domain.Managers.Persistence;

namespace CreatorPantry.Domain.Modules.Recipes.Data.Entities;

/// <summary>
/// One tag applied to one recipe. Interior to the <see cref="Recipe"/> aggregate.
/// </summary>
/// <remarks>
/// <para>
/// A set, not a list: there is no <c>SortOrder</c>, because a recipe is not "weeknight before
/// freezer-friendly". The whole row is the key — <c>(WorkspaceId, RecipeId, WorkspaceTagId)</c> — so the same
/// tag applied twice to one recipe is a primary key violation rather than a duplicate someone has to notice.
/// </para>
/// <para>
/// The two foreign keys point in different directions on purpose. Deleting a <strong>recipe</strong> removes
/// its tag links, because they are part of it. Deleting a <strong>tag</strong> is refused while any recipe
/// still carries it — a second cascade path into this table would be rejected by SQL Server anyway, and the
/// vocabulary retires entries with <see cref="WorkspaceTag.IsActive"/> instead of deleting them, so the
/// refusal costs nothing in practice and prevents silently stripping a tag from creator content.
/// </para>
/// </remarks>
public class RecipeTag : IWorkspaceOwned
{
    public Guid WorkspaceId { get; set; }

    public Guid RecipeId { get; set; }

    public Guid WorkspaceTagId { get; set; }
}

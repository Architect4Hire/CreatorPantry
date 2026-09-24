namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// Builds the string that identifies one recipe's history, so a cursor can be bound to it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The recipe is in the scope, and that is what this type exists for.</strong> Everything else about
/// this ordered set is fixed — one ordering, no filters — so without the recipe id a cursor would be
/// interchangeable between every recipe in the workspace. Page two of one recipe's history would then decode
/// cleanly against another's and return its versions from number 24 downwards, which is a wrong answer rather
/// than an error. With it, replaying one is refused as
/// <see cref="RecipeErrorCodes.CursorInvalidRequest"/>.
/// </para>
/// <para>
/// <strong>The workspace is in it too</strong>, for the reason <see cref="RecipeSearchScope"/> gives at
/// length: the ordered set a cursor names depends on the resolved workspace, and that workspace arrives
/// ambiently through the query filter rather than as a request field. Here it is belt and braces — a recipe id
/// is already workspace-unique — but a scope that omitted it would be relying on that rather than saying it.
/// </para>
/// <para>
/// <strong>The ordering is named although there is only one.</strong> If a second is ever added, cursors minted
/// before it must not silently resume in it, and a scope that never mentioned the ordering would let them.
/// </para>
/// <para>
/// No free-text part, so nothing here can contain the <c>|</c> or <c>=</c> this string is assembled from: every
/// value is a GUID or a literal.
/// </para>
/// </remarks>
public static class RecipeVersionHistoryScope
{
    /// <summary>
    /// The resource this scope belongs to. Deliberately more specific than the route's <c>versions</c> segment,
    /// which is a segment several resources could one day own; this names the one ordered set a cursor can
    /// resume. The value is hashed into every issued cursor, so changing it invalidates outstanding ones.
    /// </summary>
    public const string Resource = "recipe-versions";

    /// <summary>The one ordering this route offers: highest version number first.</summary>
    private const string Ordering = "VersionNumberDesc";

    public static string Build(Guid workspaceId, Guid recipeId) =>
        string.Join('|', $"resource={Resource}", $"workspace={workspaceId:D}", $"recipe={recipeId:D}", $"sort={Ordering}");
}

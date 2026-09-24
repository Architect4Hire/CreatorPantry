namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// Stable audit action codes for the recipe module. Renaming one orphans the history already written under
/// the old name, so these are append-only in the same way an error code is.
/// </summary>
/// <remarks>
/// <para>
/// Only the lifecycle commands appear here, and that is the current answer rather than the final one.
/// auth.md names the operations that must be audited — publishing, provider connections, member and role
/// changes, secret rotation, destructive deletion, automation approval — and none of those seams exist yet.
/// Ordinary edits are deliberately absent: a recipe's own version history already records every one of them
/// with more fidelity than an audit summary could, and duplicating that into the audit log would make the
/// log mostly noise on the day it matters.
/// </para>
/// <para>
/// Shelving a recipe is different in kind. It changes what a creator can do with the recipe and drops it out
/// of their library, it writes no version, and REC-006 asks for it to be traceable — so the audit log is the
/// only record that it happened.
/// </para>
/// </remarks>
public static class RecipeAuditActions
{
    /// <summary>
    /// What an audit entry calls the thing these actions happened to. The entity name rather than a route
    /// segment, so an entry stays readable after a route is versioned or renamed.
    /// </summary>
    public const string ResourceType = "Recipe";

    /// <summary>A recipe was moved to <see cref="RecipeStatus.Archived"/>.</summary>
    public const string Archived = "recipe.archived";

    /// <summary>A recipe was brought back from the archive.</summary>
    public const string Unarchived = "recipe.unarchived";
}

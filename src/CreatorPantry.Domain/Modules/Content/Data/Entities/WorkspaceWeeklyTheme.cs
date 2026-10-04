using CreatorPantry.Domain.Managers.Persistence;

namespace CreatorPantry.Domain.Modules.Content.Data.Entities;

/// <summary>
/// One day of the creator's own editorial week — "Meat-free Monday", "Fakeaway Friday". Workspace-owned
/// creator intellectual property: the creator writes the list, at most one theme per day, and fewer than seven
/// is the normal case.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Not reference data, and deliberately not beside <c>ContentChannelCatalog</c>.</strong> A creator
/// invents their own week, so no platform list can own it: these rows are workspace-scoped, carry the global
/// query filter like every other creator record, are never deduplicated between workspaces, and are never
/// seeded server-side. The starter names a UI may offer are prefill the creator edits or discards, which is
/// why they live in the editor and not here.
/// </para>
/// <para>
/// <strong><see cref="Key"/> is what other records store, and it is a weak reference.</strong> Nothing holds a
/// foreign key to a theme row. A consumer — a content seed, later a project — keeps the key string, so
/// retirement and deletion cannot break a record that already named a theme: a retired theme keeps its row and
/// so still resolves, and a deleted one resolves to nothing, which a consumer renders as the stored text with
/// no theme detail rather than as an error. The key is also immutable: a creator renaming
/// <see cref="DisplayName"/> leaves it alone, which is the whole reason it is stored separately from the name.
/// </para>
/// <para>
/// <strong><see cref="RetiredAt"/> is how a theme leaves the week without taking its key with it.</strong> It
/// is the per-workspace form of <c>ContentChannel.IsActive</c>: a retired theme stays readable so stored keys
/// keep resolving, is refused as a new choice, and frees its day for whatever the creator puts there next.
/// </para>
/// </remarks>
public class WorkspaceWeeklyTheme : IWorkspaceOwned
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    /// <summary>The day this theme runs on. At most one live theme per day per workspace.</summary>
    public DayOfWeek Day { get; set; }

    /// <summary>
    /// Stable, lowercase, and never reused in this workspace for a different theme — the value other records
    /// store. Supplied by the client rather than derived from <see cref="DisplayName"/>, because a key derived
    /// on every save would change the moment a creator reworded the name.
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>The creator's own words. The server stores them and does not police them.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>The creator's own note on what the day is for. Optional.</summary>
    public string? Description { get; set; }

    /// <summary>When the theme left the live week, or null while it is in it.</summary>
    public DateTimeOffset? RetiredAt { get; set; }

    /// <summary>Edit counter, starting at 1 and incremented by the write seam on every change.</summary>
    public int Revision { get; set; } = 1;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

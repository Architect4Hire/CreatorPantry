namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// One tag as a repository read of the vocabulary finds it: its id and the creator's own spelling.
/// </summary>
/// <remarks>
/// A <c>Record</c> because it does not cross a module boundary — <see cref="WorkspaceTagServiceModel"/> is what
/// the facade returns, and the two are separate so that widening the internal read never widens the published
/// one.
/// </remarks>
public sealed record WorkspaceTagRecord(Guid Id, string Name);

/// <summary>
/// One tag of the workspace's own vocabulary, as a picker reads it: an id to send and a name to show.
/// </summary>
/// <remarks>
/// <para>
/// <strong>No workspace id</strong>, which never leaves the server (tenancy.md), and no normalized name, which
/// is the natural key the server matches on and nothing a client should compare with.
/// </para>
/// <para>
/// <strong>No <c>isActive</c>.</strong> The list offers tags for new input, so it holds only active ones; a
/// retired tag an asset or recipe already carries is named by that record's own read, which is where it is
/// still true.
/// </para>
/// </remarks>
public sealed record WorkspaceTagServiceModel(Guid Id, string Name);

/// <summary>The fixed limits of reading the workspace's tag vocabulary.</summary>
public static class WorkspaceTagPolicy
{
    /// <summary>The most tags one read of the vocabulary returns.</summary>
    /// <remarks>
    /// The list is not paged, because a picker needs the whole vocabulary to filter as the creator types and a
    /// workspace's own tags are a small set. This is the ceiling that keeps "small" true of the response
    /// whatever a workspace has accumulated: past it the list is cut, alphabetically, rather than growing
    /// without limit. A workspace that reaches it needs a searched read, which is a compatible addition.
    /// </remarks>
    public const int MaxListed = 500;
}

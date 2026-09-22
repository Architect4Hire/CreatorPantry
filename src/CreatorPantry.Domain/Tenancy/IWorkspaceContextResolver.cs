namespace CreatorPantry.Domain.Tenancy;

/// <summary>
/// The write side of the request-scoped workspace context, used only by trusted resolution code (a route
/// slug lookup that has already confirmed active membership) — never by controllers, AI plugins, or
/// anything else acting on caller-supplied input directly.
/// </summary>
public interface IWorkspaceContextResolver
{
    /// <summary>
    /// Populates the context for the remainder of this scope. Every argument must already come from a
    /// verified <c>Workspace</c>/<c>WorkspaceMembership</c> lookup, not from a request body, query
    /// string, header, or AI tool argument.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="workspaceId"/> or <paramref name="membershipId"/>
    /// is <see cref="Guid.Empty"/>, or <paramref name="workspaceSlug"/> is null or whitespace.</exception>
    /// <exception cref="InvalidOperationException">This scope was already resolved. A request never
    /// switches workspace mid-flight.</exception>
    void Resolve(Guid workspaceId, string workspaceSlug, Guid membershipId, WorkspaceRole role);
}

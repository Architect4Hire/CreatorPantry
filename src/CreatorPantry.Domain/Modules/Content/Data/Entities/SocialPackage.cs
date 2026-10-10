using CreatorPantry.Domain.Managers.Persistence;

namespace CreatorPantry.Domain.Modules.Content.Data.Entities;

/// <summary>
/// The posts written for one piece of creative work (AF.6.1, baseline B-27). Workspace-owned, and the root of
/// the post aggregate.
/// </summary>
/// <remarks>
/// <para>
/// <strong>One per creative context, and a holder only.</strong> It carries no body, no pins and no status of
/// its own: each channel the creator picked is a <see cref="SocialPackageChannel"/> under it, decided on its
/// own, and every body is an immutable <see cref="SocialRevision"/> under that channel. A package-level status
/// would be a second statement of what its channels already say.
/// </para>
/// <para>
/// <strong>A derivative, never canonical.</strong> Nothing here writes back to a recipe or to the context it
/// belongs to, and nothing here names a provider: delivery belongs to <c>Publication</c> records.
/// </para>
/// </remarks>
public class SocialPackage : IWorkspaceOwned
{
    public Guid Id { get; set; }

    /// <summary>Stamped by <c>WorkspaceOwnershipInterceptor</c> from the resolved context, never by hand.</summary>
    public Guid WorkspaceId { get; set; }

    /// <summary>The piece of work these posts are for. A context has at most one package.</summary>
    public Guid CreativeContextId { get; set; }

    /// <summary>Not a foreign key: authorship outlives membership, as elsewhere.</summary>
    public Guid CreatedByMembershipId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public List<SocialPackageChannel> Channels { get; set; } = [];
}

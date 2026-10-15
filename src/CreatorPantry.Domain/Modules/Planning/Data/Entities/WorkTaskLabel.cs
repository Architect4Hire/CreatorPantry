using CreatorPantry.Domain.Managers.Persistence;

namespace CreatorPantry.Domain.Modules.Planning.Data.Entities;

/// <summary>One task carrying one of the workspace's tags.</summary>
/// <remarks>
/// <para>
/// It links <c>WorkspaceTag</c>, the vocabulary recipes and assets already share, for <c>MediaAssetTag</c>'s
/// reason: a word the creator uses is one word everywhere, and a second vocabulary makes it two that merely look
/// alike. The tag entity lives in the Recipes module; only its type crosses, because a foreign key does.
/// </para>
/// <para>
/// Interior to its task for deletion: a composite foreign key into the task, which cascades, and none to
/// <c>Workspaces</c>, so SQL Server sees one delete path. The tag side is restricted, and tags are retired rather
/// than deleted.
/// </para>
/// </remarks>
public class WorkTaskLabel : IWorkspaceOwned
{
    public Guid WorkspaceId { get; set; }

    public Guid WorkTaskId { get; set; }

    public Guid WorkspaceTagId { get; set; }
}

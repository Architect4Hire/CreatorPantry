using CreatorPantry.Domain.Managers.Persistence;

namespace CreatorPantry.Domain.Modules.Ai.Data.Entities;

/// <summary>
/// One workspace's own answer to whether one AI task is switched on.
/// </summary>
/// <remarks>
/// A row exists only once an Owner has decided. A task with no row falls back to the deployment's
/// <c>Ai:Tasks:Enabled</c> default, so absence means "nobody here has chosen", never "off".
/// </remarks>
public class WorkspaceAiTaskSwitch : IWorkspaceOwned
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    /// <summary>The task's discriminator from <c>AiTaskCatalog</c>, e.g. <c>recipe.revision</c>.</summary>
    public string Task { get; set; } = string.Empty;

    public bool IsEnabled { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

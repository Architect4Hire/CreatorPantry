namespace CreatorPantry.Domain.Managers.Persistence;

/// <summary>
/// Marks an entity as workspace-owned creator IP. Always has a required, indexed <see cref="WorkspaceId"/>
/// and is subject to the global EF Core query filter derived from <see cref="CreatorPantry.Domain.Managers.Persistence.IWorkspaceContext"/>.
/// </summary>
public interface IWorkspaceOwned
{
    Guid WorkspaceId { get; set; }
}

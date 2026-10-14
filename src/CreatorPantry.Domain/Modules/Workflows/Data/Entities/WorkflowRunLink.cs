using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Workflows.Managers;

namespace CreatorPantry.Domain.Modules.Workflows.Data.Entities;

/// <summary>A domain record a run started from or produced, by kind and id.</summary>
/// <remarks>
/// <para>
/// <strong>Deliberately not a foreign key.</strong> A link can name any kind the contract knows, and a foreign
/// key per kind would let a run either cascade into a recipe or stand in the way of archiving one — both
/// things a guided journey has no business doing. A link to a record that is no longer there is a
/// recoverable warning when the run is read (13A.6), not an error here.
/// </para>
/// <para>
/// Interior to its run for deletion, like <see cref="WorkflowStepRun"/>.
/// </para>
/// </remarks>
public class WorkflowRunLink : IWorkspaceOwned
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    public Guid WorkflowRunId { get; set; }

    public WorkflowResourceKind ResourceKind { get; set; }

    public Guid ResourceId { get; set; }

    /// <summary>The step that produced it, or null for the record the run started from (13A.5).</summary>
    public string? StepKey { get; set; }

    public DateTimeOffset LinkedAt { get; set; }

    public Guid LinkedByMembershipId { get; set; }
}

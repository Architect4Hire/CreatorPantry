using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Workflows.Managers;

namespace CreatorPantry.Domain.Modules.Workflows.Data.Entities;

/// <summary>
/// One creator's journey through one pinned <see cref="WorkflowDefinition"/> version (FLOW-002).
/// </summary>
/// <remarks>
/// <para>
/// <strong>State and pointers only.</strong> A run records where the creator has got to and which records the
/// journey touched, by kind and id. It never holds a recipe, article or picture, nor any prompt or provider
/// detail: those stay with the modules that own them, and a run that outlives one of them shows a warning
/// rather than a stale copy.
/// </para>
/// <para>
/// <strong>The aggregate root, and the only row with a concurrency token.</strong> Every write to a step or a
/// link also writes the run — its <see cref="UpdatedAt"/>, its current step, its counts — so the one
/// <see cref="RowVersion"/> refuses a stale write to any part of it.
/// </para>
/// <para>
/// <strong>People are memberships, not users.</strong> A membership belongs to one workspace, so neither id can
/// name someone outside it. Neither is a foreign key, as no actor id in this schema is: removing a member
/// neither deletes their runs nor is blocked by them.
/// </para>
/// </remarks>
public class WorkflowRun : IWorkspaceOwned
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    /// <summary>The definition this run follows, never upgraded under it (13A.6).</summary>
    public string DefinitionKey { get; set; } = string.Empty;

    public int DefinitionVersion { get; set; }

    /// <summary>What the creator calls this run, or the definition's goal when they named nothing.</summary>
    public string Title { get; set; } = string.Empty;

    public WorkflowRunStatus Status { get; set; }

    /// <summary>The step the creator is on. Required while in progress, and none once completed.</summary>
    public string? CurrentStepKey { get; set; }

    /// <summary>The pinned definition's step count, fixed at start because the version is.</summary>
    public int StepCount { get; set; }

    /// <summary>Kept beside the step rows so a list of runs never has to read them (13A.11).</summary>
    public int CompletedStepCount { get; set; }

    public int SkippedStepCount { get; set; }

    /// <summary>
    /// Where the creator last was, relative to the workspace (<c>recipes/{id}</c>). Never absolute and never a
    /// scheme: the configuration refuses both, and 13A.10 holds it to the run's own capability routes.
    /// </summary>
    public string? LastRoute { get; set; }

    /// <summary>Who started it, and so who owns it.</summary>
    public Guid CreatedByMembershipId { get; set; }

    /// <summary>Who it is for, when that is someone in particular.</summary>
    public Guid? AssignedMembershipId { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public DateTimeOffset? AbandonedAt { get; set; }

    /// <summary>
    /// The run that replaced this one when the creator started over (13A.9b); set only on a run abandoned that way.
    /// A pointer, not a foreign key — the server sets it, in the same workspace and the same write, to a run it has
    /// just created — so no run can cascade into, or be held by, another.
    /// </summary>
    public Guid? ReplacedByRunId { get; set; }

    public byte[] RowVersion { get; set; } = [];
}

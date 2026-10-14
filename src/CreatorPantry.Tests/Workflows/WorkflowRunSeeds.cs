using CreatorPantry.Domain.Modules.Workflows.Data.Entities;
using CreatorPantry.Domain.Modules.Workflows.Managers;

namespace CreatorPantry.Tests.Workflows;

/// <summary>
/// Well-formed workflow rows for the model tests. WorkspaceId is left unset throughout: stamping it is the
/// ownership interceptor's job, so a row lands in whichever workspace the scope resolved.
/// </summary>
internal static class WorkflowRunSeeds
{
    public static WorkflowRun Run(DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        DefinitionKey = "recipe.create",
        DefinitionVersion = 1,
        Title = "Create a recipe",
        Status = WorkflowRunStatus.InProgress,
        CurrentStepKey = "start",
        StepCount = 5,
        CreatedByMembershipId = Guid.NewGuid(),
        StartedAt = now,
        UpdatedAt = now,
    };

    public static WorkflowStepRun Step(WorkflowRun run, string key) => new()
    {
        Id = Guid.NewGuid(),
        WorkflowRunId = run.Id,
        StepKey = key,
        Status = WorkflowStepRunStatus.NotStarted,
    };

    public static WorkflowRunLink Link(WorkflowRun run, DateTimeOffset now, Guid? resourceId = null, string? stepKey = "start") => new()
    {
        Id = Guid.NewGuid(),
        WorkflowRunId = run.Id,
        ResourceKind = WorkflowResourceKind.Recipe,
        ResourceId = resourceId ?? Guid.NewGuid(),
        StepKey = stepKey,
        LinkedAt = now,
        LinkedByMembershipId = run.CreatedByMembershipId,
    };
}

namespace CreatorPantry.Domain.Modules.Workflows.Managers;

/// <summary>The audit codes workflow runs record.</summary>
public static class WorkflowAuditActions
{
    public const string RunResourceType = "WorkflowRun";

    /// <summary>
    /// A run stopped by someone with the right to (13A.10b) — who may be an Editor stopping another person's run, which
    /// cannot be undone. Ids and counts only: the reason is the creator's own words, and stays on the run.
    /// </summary>
    public const string RunAbandoned = "workflows.run.abandoned";
}

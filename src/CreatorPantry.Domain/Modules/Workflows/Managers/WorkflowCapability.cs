using CreatorPantry.Domain.Managers.Persistence;

namespace CreatorPantry.Domain.Modules.Workflows.Managers;

/// <summary>An implemented product capability a workflow step can send the creator to.</summary>
/// <param name="Key">Stable identity, e.g. <c>recipes.edit</c>.</param>
/// <param name="Name">The action as a creator reads it on a button: "Write it yourself".</param>
/// <param name="RouteTemplate">
/// The browser route under the workspace, without a leading slash, e.g. <c>recipes/{recipeId}</c>. Its only
/// placeholders are those in <see cref="WorkflowResourceKinds.RoutePlaceholders"/>, filled from the run's links.
/// </param>
/// <param name="MinimumRole">
/// The least role that can use it, so the hub can say so up front (13A.13). Advice only: the capability's own
/// endpoints remain the authority.
/// </param>
/// <param name="UnavailableReason">
/// Null when the capability exists. Otherwise, why it cannot be used yet, in ordinary language. A required
/// step may not rely on an unavailable capability.
/// </param>
/// <param name="DraftKind">
/// The kind of record this capability keeps unfinished work in, which a step on it may point to as its draft
/// (13A.7) — or null when there is nothing to point to: the work is already linked to the run, there is nothing to
/// keep, or the screen keeps its own place. Declared here, where the work happens, rather than on each step.
/// </param>
public sealed record WorkflowCapability(
    WorkflowCapabilityKey Key,
    string Name,
    string RouteTemplate,
    WorkspaceRole MinimumRole,
    string? UnavailableReason = null,
    WorkflowResourceKind? DraftKind = null)
{
    public bool IsAvailable => UnavailableReason is null;
}

/// <summary>A step's capability: one primary action and at most two alternatives, most recommended first.</summary>
/// <remarks>
/// "Start from scratch" or "Help me draft it" are two ways to do one step, not two steps. The primary is the one
/// obvious action the easy-as-pie rule asks for; the alternatives are offered beside it.
/// </remarks>
public sealed record WorkflowCapabilityLink(WorkflowCapabilityKey Primary, IReadOnlyList<WorkflowCapabilityKey> Alternatives)
{
    public WorkflowCapabilityLink(WorkflowCapabilityKey primary)
        : this(primary, [])
    {
    }
}

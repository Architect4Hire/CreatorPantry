using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Workflows.Data.Entities;
using CreatorPantry.Domain.Modules.Workflows.Managers;

namespace CreatorPantry.Domain.Modules.Workflows.Business;

/// <summary>
/// Who may start a workflow, and what a finished run suggests doing next — one rule for the definition list (13A.13)
/// and a run's suggestions (13A.14), so a "Start" on either is one a start will accept.
/// </summary>
internal static class WorkflowStartRules
{
    /// <summary>The least role the start route takes from anyone.</summary>
    public const WorkspaceRole LeastToStart = WorkspaceRole.Contributor;

    /// <summary>
    /// Whoever could take the definition's first step, and never less than a start takes; null when none of its
    /// actions is built.
    /// </summary>
    public static WorkspaceRole? NeededRole(WorkflowDefinition definition)
    {
        var first = definition.Steps[0].Capability;
        var actions = new[] { first.Primary }.Concat(first.Alternatives)
            .Select(key => WorkflowCapabilities.ByKey[key])
            .Where(capability => capability.IsAvailable)
            .ToList();

        return actions.Count == 0
            ? null
            : (WorkspaceRole)Math.Max((int)actions.Min(capability => capability.MinimumRole), (int)LeastToStart);
    }

    /// <summary>
    /// A definition's next suggestions as this reader could act on them, resolved against what the run holds. A
    /// workflow is offered the newest record of a kind it starts from, and is available and the reader's to start by
    /// the same rule as the definition list — workspace facts it waits on are the start's to say. A capability gets
    /// its address and the reader's role against its own. Suggested workflows are read at their newest version,
    /// which is what a new run starts on.
    /// </summary>
    public static IReadOnlyList<WorkflowSuggestionServiceModel> Suggestions(
        WorkflowDefinition definition,
        IReadOnlyList<WorkflowRunLink> links,
        WorkspaceRole role,
        IWorkflowCatalogue catalogue)
    {
        var latest = links
            .GroupBy(link => link.ResourceKind)
            .ToDictionary(family => family.Key, family => family.OrderBy(link => link.LinkedAt).Last().ResourceId);

        return
        [
            .. definition.NextSuggestions.Select(suggestion => suggestion switch
            {
                WorkflowSuggestion.Workflow workflow => ForWorkflow(workflow, latest, role, catalogue),
                WorkflowSuggestion.Capability capability => ForCapability(capability, latest, role),
                _ => throw new NotSupportedException($"Unknown suggestion {suggestion.GetType().Name}."),
            }),
        ];
    }

    private static WorkflowSuggestionServiceModel ForWorkflow(
        WorkflowSuggestion.Workflow suggestion,
        IReadOnlyDictionary<WorkflowResourceKind, Guid> latest,
        WorkspaceRole role,
        IWorkflowCatalogue catalogue)
    {
        // The catalogue validator guarantees every suggested workflow exists.
        var target = catalogue.Latest().Single(definition => definition.Key == suggestion.Key);
        var from = target.StartsFrom.Where(latest.ContainsKey).Cast<WorkflowResourceKind?>().FirstOrDefault();

        return new WorkflowSuggestionServiceModel
        {
            Kind = WorkflowSuggestionKind.Workflow,
            Key = target.Key.Value,
            Label = target.Goal,
            Why = suggestion.Why,
            StartFromKind = from,
            StartFromId = from is { } kind ? latest[kind] : null,
            Available = target.IsAvailable,
            YouCanDoIt = target.IsAvailable && NeededRole(target) is { } needed && role >= needed,
        };
    }

    private static WorkflowSuggestionServiceModel ForCapability(
        WorkflowSuggestion.Capability suggestion, IReadOnlyDictionary<WorkflowResourceKind, Guid> latest, WorkspaceRole role)
    {
        var capability = WorkflowCapabilities.ByKey[suggestion.Key];

        return new WorkflowSuggestionServiceModel
        {
            Kind = WorkflowSuggestionKind.Capability,
            Key = capability.Key.Value,
            Label = capability.Name,
            Why = suggestion.Why,
            Route = WorkflowRoutes.Resolve(capability.RouteTemplate, latest),
            Available = capability.IsAvailable,
            YouCanDoIt = capability.IsAvailable && role >= capability.MinimumRole,
        };
    }
}

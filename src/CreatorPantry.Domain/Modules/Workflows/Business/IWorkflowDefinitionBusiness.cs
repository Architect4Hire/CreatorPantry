using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Workflows.Managers;

namespace CreatorPantry.Domain.Modules.Workflows.Business;

public interface IWorkflowDefinitionBusiness
{
    /// <summary>The newest version of every workflow, in the catalogue's order, as this reader could start it (13A.13).</summary>
    Task<IReadOnlyList<WorkflowDefinitionSummaryServiceModel>> ListAsync(CancellationToken cancellationToken);
}

/// <remarks>
/// <para>
/// <strong>The same rules a start applies, asked in advance.</strong> A workflow can be started when it is built, when
/// nothing it needs first is missing, and when the reader can take its first step — a Contributor at least, as the
/// start route requires, and whatever more that step's actions need. A card that says "Start" is one a start will
/// accept.
/// </para>
/// <para>
/// <strong>Fixed cost.</strong> The catalogue is in memory; each workspace fact is asked of the module that owns it
/// once per request, however many workflows wait on it.
/// </para>
/// </remarks>
internal sealed class WorkflowDefinitionBusiness(
    IWorkflowCatalogue catalogue,
    IWorkspaceContext workspace,
    IWorkflowFacts facts) : IWorkflowDefinitionBusiness
{
    public async Task<IReadOnlyList<WorkflowDefinitionSummaryServiceModel>> ListAsync(CancellationToken cancellationToken)
    {
        var known = new Dictionary<WorkflowWorkspaceFact, bool>();
        var summaries = new List<WorkflowDefinitionSummaryServiceModel>();

        foreach (var definition in catalogue.Latest())
        {
            var waitingFor = new List<WorkflowStartNeedServiceModel>();
            foreach (var prerequisite in definition.Prerequisites)
            {
                switch (prerequisite)
                {
                    // Started from nothing, a run holds no record yet.
                    case WorkflowPrerequisite.ResourceLinked linked:
                        waitingFor.Add(new WorkflowStartNeedServiceModel
                        {
                            Message = $"This needs {Noun(linked.Kind)} first.",
                            Route = LibraryRoute(linked.Kind),
                        });
                        break;

                    case WorkflowPrerequisite.WorkspaceFact { Fact: WorkflowWorkspaceFact.SchedulingZoneSet } fact:
                        if (!known.TryGetValue(fact.Fact, out var set))
                        {
                            set = await facts.SchedulingZoneSetAsync(cancellationToken);
                            known[fact.Fact] = set;
                        }

                        if (!set)
                        {
                            waitingFor.Add(new WorkflowStartNeedServiceModel
                            {
                                Message = "Set your workspace’s time zone in brand settings first.",
                                Route = "brand",
                            });
                        }

                        break;
                }
            }

            // Whoever could take the first step, and never less than a start takes.
            var needed = WorkflowStartRules.NeededRole(definition);
            var roleAllows = needed is { } least && workspace.Role >= least;

            summaries.Add(new WorkflowDefinitionSummaryServiceModel
            {
                Key = definition.Key.Value,
                Version = definition.Version,
                Goal = definition.Goal,
                Description = definition.Description,
                ExpectedOutcome = definition.ExpectedOutcome,
                StartsFrom = definition.StartsFrom,
                Available = definition.IsAvailable,
                UnavailableReason = definition.UnavailableReason,
                CanStart = definition.IsAvailable && roleAllows && waitingFor.Count == 0,
                NeededRole = definition.IsAvailable && needed is { } role && !roleAllows ? role : null,
                WaitingFor = waitingFor,
            });
        }

        return summaries;
    }

    private static string Noun(WorkflowResourceKind kind) => kind switch
    {
        WorkflowResourceKind.Recipe => "a recipe",
        WorkflowResourceKind.MediaAsset => "a picture",
        WorkflowResourceKind.ContentCard => "a card on your board",
        WorkflowResourceKind.BrandStyleGuide => "a brand guide",
        WorkflowResourceKind.LongFormDraft => "a piece of writing",
        WorkflowResourceKind.CreativeContext => "an idea",
        _ => "something else",
    };

    /// <summary>Where in the workspace a record of this kind is made or found, or null when there is no such page.</summary>
    private static string? LibraryRoute(WorkflowResourceKind kind) => kind switch
    {
        WorkflowResourceKind.Recipe => "recipes",
        WorkflowResourceKind.MediaAsset => "dam",
        WorkflowResourceKind.ContentCard => "content-board",
        WorkflowResourceKind.BrandStyleGuide => "brand",
        _ => null,
    };
}

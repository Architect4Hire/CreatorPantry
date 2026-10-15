using CreatorPantry.Domain.Managers.Persistence;

namespace CreatorPantry.Domain.Modules.Workflows.Managers;

/// <summary>
/// One workflow as the hub offers it (13A.13): what it is for in plain language, and whether this reader can start it
/// now — and if not, why, and where to fix it. No capability, step or model detail.
/// </summary>
public sealed record WorkflowDefinitionSummaryServiceModel
{
    public required string Key { get; init; }

    /// <summary>The newest version, which a start begins on.</summary>
    public required int Version { get; init; }

    public required string Goal { get; init; }

    public required string Description { get; init; }

    public required string ExpectedOutcome { get; init; }

    /// <summary>The kinds of record it can be started from elsewhere in the product. Starting from none is always allowed.</summary>
    public required IReadOnlyList<WorkflowResourceKind> StartsFrom { get; init; }

    /// <summary>False for a workflow not built yet; <see cref="UnavailableReason"/> says so in plain words.</summary>
    public required bool Available { get; init; }

    public string? UnavailableReason { get; init; }

    /// <summary>Whether a start by this reader, now, from nothing, would be accepted.</summary>
    public required bool CanStart { get; init; }

    /// <summary>The least role that could start it, when someone could and this reader cannot; otherwise null.</summary>
    public WorkspaceRole? NeededRole { get; init; }

    /// <summary>What has to be true first, each said plainly with the page that sorts it out.</summary>
    public required IReadOnlyList<WorkflowStartNeedServiceModel> WaitingFor { get; init; }
}

/// <summary>Something a workflow needs before it can start, and where in the workspace to see to it.</summary>
public sealed record WorkflowStartNeedServiceModel
{
    public required string Message { get; init; }

    /// <summary>A page relative to the workspace (<c>brand</c>, <c>recipes</c>), or null when there is none to send them to.</summary>
    public string? Route { get; init; }
}

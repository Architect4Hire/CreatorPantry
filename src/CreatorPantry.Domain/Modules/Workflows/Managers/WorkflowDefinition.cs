namespace CreatorPantry.Domain.Modules.Workflows.Managers;

/// <summary>
/// One guided journey toward a plain-language goal, as code shipped with the application (FLOW-001).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Orchestration, not domain.</strong> A definition names capabilities that already exist and the facts
/// that say a step is done; it never re-states a domain rule. A step is finished when the recipe module's own
/// readiness evaluation says so, not when this file counts ingredient lines.
/// </para>
/// <para>
/// <strong>Versioned with the application.</strong> <see cref="Key"/> never changes meaning, and
/// <see cref="Version"/> rises by one on <em>any</em> change — copy included. A run pins the version it started
/// on (13A.6), so a shipped version is frozen and is never removed while the application can still resume it.
/// </para>
/// <para>
/// <strong>No expressions.</strong> Prerequisites, completion rules and suggestions are closed record
/// hierarchies whose every member is data. Nothing here is evaluated by interpreting a string.
/// </para>
/// </remarks>
public sealed record WorkflowDefinition
{
    public required WorkflowKey Key { get; init; }

    /// <summary>1, 2, 3… Raised by one on any change to the definition.</summary>
    public required int Version { get; init; }

    /// <summary>What the creator wants to get done, as they would say it: "Create a recipe".</summary>
    public required string Goal { get; init; }

    public required string Description { get; init; }

    /// <summary>What the creator has at the end: "A recipe in your library … ready to test."</summary>
    public required string ExpectedOutcome { get; init; }

    /// <summary>What must be true before the journey can start.</summary>
    public IReadOnlyList<WorkflowPrerequisite> Prerequisites { get; init; } = [];

    /// <summary>
    /// The kinds of record a run may start from (13A.5): "improve this recipe", "organize this picture". Empty for
    /// a journey that always starts from nothing.
    /// </summary>
    /// <remarks>
    /// A run starts from at most one record, checked through its owning module before anything is written. When
    /// the first step's whole job is to link a record of exactly that kind, starting from one completes it — a
    /// fact the server checked, never the client's word. Nothing is created to stand in for a record that is not
    /// there.
    /// </remarks>
    public IReadOnlyList<WorkflowResourceKind> StartsFrom { get; init; } = [];

    /// <summary>The steps, in the order they are taken. The order is the next action; it is not authored again.</summary>
    public required IReadOnlyList<WorkflowStepDefinition> Steps { get; init; }

    /// <summary>What to do once the journey is complete, most recommended first. At most three.</summary>
    public IReadOnlyList<WorkflowSuggestion> NextSuggestions { get; init; } = [];

    /// <summary>
    /// Null for a journey a creator can take. Otherwise why they cannot yet, in ordinary language — shown in place
    /// of a way in (13A.13), and the reason a start is refused (13A.5).
    /// </summary>
    /// <remarks>
    /// The one way a definition may rest a required step on a capability that is not built: by saying so here.
    /// The validator holds it to that both ways — a reason with nothing actually unavailable is an error too, so
    /// a journey cannot be hidden behind a stale excuse once its pieces exist.
    /// </remarks>
    public string? UnavailableReason { get; init; }

    public bool IsAvailable => UnavailableReason is null;
}

/// <summary>One step of a <see cref="WorkflowDefinition"/>.</summary>
public sealed record WorkflowStepDefinition
{
    public required WorkflowStepKey Key { get; init; }

    public required string Title { get; init; }

    /// <summary>What the step needs and why, in ordinary language.</summary>
    public required string Help { get; init; }

    public required WorkflowStepRequirement Requirement { get; init; }

    /// <summary>The existing capability that does the step's work.</summary>
    public required WorkflowCapabilityLink Capability { get; init; }

    public IReadOnlyList<WorkflowPrerequisite> Prerequisites { get; init; } = [];

    /// <summary>The server-checked fact that says the step is done.</summary>
    public required WorkflowCompletionRule CompletionRule { get; init; }
}

public enum WorkflowStepRequirement
{
    /// <summary>The run cannot complete without it.</summary>
    Required = 1,

    /// <summary>May be skipped (13A.9a). Skipping records a skip and never fabricates the step's result.</summary>
    Optional = 2,
}

namespace CreatorPantry.Domain.Modules.Workflows.Managers;

/// <summary>One run as a list shows it (13A.11): pointers, counts and names from the catalogue — never a body.</summary>
/// <remarks>
/// No step's draft, no reason a creator gave, no route and nothing a linked record holds: those are the run page's
/// (13A.6), which a list item opens. Everything here is read from the run's own row and the catalogue, plus one
/// linked record's kind and id.
/// </remarks>
public sealed record WorkflowRunSummaryServiceModel
{
    public required Guid Id { get; init; }

    public required string Title { get; init; }

    public required WorkflowRunStatus Status { get; init; }

    public required string DefinitionKey { get; init; }

    /// <summary>The version the run is pinned to.</summary>
    public required int DefinitionVersion { get; init; }

    /// <summary>What the workflow is for, from that version.</summary>
    public required string Goal { get; init; }

    /// <summary>Who started it, and so who owns it.</summary>
    public required WorkflowRunMemberServiceModel Owner { get; init; }

    /// <summary>Who it is for, when that is someone in particular.</summary>
    public WorkflowRunMemberServiceModel? Assignee { get; init; }

    public required int StepCount { get; init; }

    public required int CompletedStepCount { get; init; }

    public required int SkippedStepCount { get; init; }

    /// <summary>The step it is on, or the one it was left on when stopped; null once completed.</summary>
    public WorkflowRunStepReferenceServiceModel? CurrentStep { get; init; }

    /// <summary>The record it started from, or else the last one it made; null when it holds none.</summary>
    public WorkflowRunResourceReferenceServiceModel? LinkedResource { get; init; }

    /// <summary>What to do next, while it is in progress and on a step; null otherwise.</summary>
    public WorkflowRunSummaryActionServiceModel? NextAction { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public DateTimeOffset? AbandonedAt { get; init; }

    /// <summary>
    /// Why an in-progress run needs a look — codes from <see cref="WorkflowRunAttentionReasons"/>; empty when nothing
    /// does, and always empty for a run that is not in progress.
    /// </summary>
    public IReadOnlyList<string> AttentionReasons { get; init; } = [];
}

/// <summary>A member of the workspace by membership, with their name when they are still a member.</summary>
public sealed record WorkflowRunMemberServiceModel
{
    public required Guid MembershipId { get; init; }

    /// <summary>Null for someone who has since left the workspace.</summary>
    public string? DisplayName { get; init; }
}

public sealed record WorkflowRunStepReferenceServiceModel
{
    public required string Key { get; init; }

    public required string Title { get; init; }
}

/// <summary>A record by kind and id. Its name is its own module's to give; the run page has the detail.</summary>
public sealed record WorkflowRunResourceReferenceServiceModel
{
    public required WorkflowResourceKind Kind { get; init; }

    public required Guid Id { get; init; }
}

/// <summary>The next step and its main action, as the reader could take it.</summary>
public sealed record WorkflowRunSummaryActionServiceModel
{
    public required string StepKey { get; init; }

    public required string Title { get; init; }

    /// <summary>The step's main action's name.</summary>
    public required string CapabilityName { get; init; }

    /// <summary>Whether the reader can take this step's main action, or one of its alternatives.</summary>
    public required bool YouCanDoIt { get; init; }
}

/// <summary>Why an in-progress run needs a look. Treat as an open set.</summary>
public static class WorkflowRunAttentionReasons
{
    /// <summary>No one has touched it in <see cref="WorkflowRunPolicy.IdleAfter"/>.</summary>
    public const string Idle = "idle";

    /// <summary>A newer version of its workflow has shipped since it started; it carries on as it began.</summary>
    public const string NewerVersion = "newer_version";

    /// <summary>The step it is on needs someone with more access than the reader has.</summary>
    public const string NotYoursToDo = "not_yours_to_do";

    /// <summary>
    /// The version this run is pinned to is no longer shipped, so its steps are unknown. The run is still the
    /// creator's and is still listed: a catalogue release does not take their work with it.
    /// </summary>
    public const string VersionGone = "version_gone";
}

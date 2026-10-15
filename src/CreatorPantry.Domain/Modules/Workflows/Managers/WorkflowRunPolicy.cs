namespace CreatorPantry.Domain.Modules.Workflows.Managers;

/// <summary>Column limits for workflow runs, shared by the configuration and, later, the validators.</summary>
public static class WorkflowRunPolicy
{
    /// <summary>A definition or step key, as <see cref="WorkflowDefinitionValidator"/> allows it.</summary>
    public const int KeyMaxLength = WorkflowDefinitionValidator.MaxKeyLength;

    public const int TitleMaxLength = 120;

    public const int LastRouteMaxLength = 400;

    public const int SkipReasonMaxLength = 500;

    public const int AbandonReasonMaxLength = 500;

    /// <summary>How long a run in progress may stand untouched before a list says it needs a look (13A.11).</summary>
    public static readonly TimeSpan IdleAfter = TimeSpan.FromDays(7);
}

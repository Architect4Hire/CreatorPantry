namespace CreatorPantry.Domain.Modules.Workflows.Managers;

/// <summary>Where a whole run stands. Saving and leaving is not a status: a run left part-way is still in progress.</summary>
public enum WorkflowRunStatus
{
    /// <summary>Started and not finished, whether or not anyone has it open.</summary>
    InProgress = 1,

    /// <summary>Every required step's completion rule was satisfied when the server checked it (13A.10a).</summary>
    Completed = 2,

    /// <summary>The creator chose to stop (13A.10b). Its links are kept; nothing it linked is touched.</summary>
    Abandoned = 3,
}

/// <summary>Where one step of a run stands.</summary>
public enum WorkflowStepRunStatus
{
    NotStarted = 1,

    InProgress = 2,

    /// <summary>The step's completion rule was satisfied when the server checked it (13A.8).</summary>
    Completed = 3,

    /// <summary>
    /// Passed over. Only a step its definition declares optional may be (13A.9a), and the database cannot know
    /// which those are, so that rule is the business layer's.
    /// </summary>
    Skipped = 4,
}

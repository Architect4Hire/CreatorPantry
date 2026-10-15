namespace CreatorPantry.Domain.Modules.Workflows.Managers;

/// <summary>The stable codes the workflow-run routes answer with.</summary>
/// <remarks>
/// The HTTP status follows from each code's suffix (<c>ProblemResults.StatusFor</c>), so a code says both what
/// happened and how it is answered: a bare <c>.unavailable</c> would read as a dependency that is down (503), which
/// an unbuilt journey is not.
/// </remarks>
public static class WorkflowErrorCodes
{
    /// <summary>404: no such run in this workspace — the same answer for an unknown id and another workspace's run.</summary>
    public const string RunNotFound = "workflows.run.not_found";

    /// <summary>404: the run's pinned version has no step by that key.</summary>
    public const string StepNotFound = "workflows.step.not_found";

    /// <summary>409: the run is completed or abandoned, so nothing about it changes any more.</summary>
    public const string RunNotInProgress = "workflows.run.not_in_progress.conflict";

    /// <summary>400: a Start over was not confirmed, or was not shaped as one can be.</summary>
    public const string RunRestartInvalid = "workflows.run.restart.invalid";

    /// <summary>400: a stop was not confirmed, or was not shaped as one can be.</summary>
    public const string RunAbandonInvalid = "workflows.run.abandon.invalid";

    /// <summary>400: a skip was not shaped as one can be.</summary>
    public const string StepSkipInvalid = "workflows.step.skip.invalid";

    /// <summary>422: the step is required by its workflow, so it cannot be passed over.</summary>
    public const string StepRequired = "workflows.step.required.unprocessable";

    /// <summary>409: the step is already done; going back to it reopens it.</summary>
    public const string StepAlreadyDone = "workflows.step.already_done.conflict";

    /// <summary>409: Back from the first step — there is no step before it.</summary>
    public const string AtFirstStep = "workflows.run.at_first_step.conflict";

    /// <summary>400: a Back was not shaped as one can be.</summary>
    public const string BackInvalid = "workflows.step.back.invalid";

    /// <summary>400: a run's completion was not shaped as one can be.</summary>
    public const string RunCompletionInvalid = "workflows.run.completion.invalid";

    /// <summary>
    /// 422: a required step is not done, or its rule no longer holds; <c>incompleteSteps</c> says which, and what
    /// stands in the way, in plain words.
    /// </summary>
    public const string RunIncomplete = "workflows.run.incomplete.unprocessable";

    /// <summary>400: a save and exit was not shaped as one can be, or named an action the step does not offer.</summary>
    public const string SaveAndExitInvalid = "workflows.run.save_and_exit.invalid";

    /// <summary>422: the action named has no page to come back to for this run — not built yet, or a record it opens is missing.</summary>
    public const string LastRouteUnprocessable = "workflows.run.last_route.unprocessable";

    /// <summary>409: the step is not the one the run is on.</summary>
    public const string StepNotCurrent = "workflows.step.not_current.conflict";

    /// <summary>409: the run changed since the caller read it; the answer carries what it is now.</summary>
    public const string RunConflict = "workflows.run.conflict";

    /// <summary>400: the completion was not shaped as one can be, or named a record this step does not make.</summary>
    public const string StepCompletionInvalid = "workflows.step.completion.invalid";

    /// <summary>422: the step's rule is not met yet; <c>missing</c> says what stands in the way, in plain words.</summary>
    public const string StepIncomplete = "workflows.step.incomplete.unprocessable";

    /// <summary>422: a result is not one this workspace has — one answer for unknown and another's.</summary>
    public const string StepResultUnprocessable = "workflows.step.result.unprocessable";

    /// <summary>400: the draft was not shaped as one can be; the field errors say where.</summary>
    public const string StepDraftInvalid = "workflows.step.draft.invalid";

    /// <summary>422: the draft's record is not one this workspace has — one answer for unknown and another's.</summary>
    public const string StepDraftUnprocessable = "workflows.step.draft.unprocessable";

    /// <summary>400: the request was not shaped as a start can be; the field errors say where.</summary>
    public const string RunInvalid = "workflows.run.invalid";

    /// <summary>
    /// 403 from the facade: a Viewer, or anyone below the least role a start needs. Over HTTP the route's policy
    /// refuses them first with the edge's generic <c>forbidden</c>; this code is what any other caller sees.
    /// </summary>
    public const string RunForbidden = "workflows.run.forbidden";

    /// <summary>422: the journey exists but cannot be taken yet; the answer carries its reason.</summary>
    public const string DefinitionUnavailable = "workflows.definition.unavailable.unprocessable";

    /// <summary>403: the caller cannot take the journey's first action, nor any alternative to it.</summary>
    public const string RoleInsufficient = "workflows.run.role_insufficient.forbidden";

    /// <summary>422: the starting record is not one this workspace has — one answer for unknown and another's.</summary>
    public const string StartingResourceUnprocessable = "workflows.run.starting_resource.unprocessable";

    /// <summary>422: something the journey needs before it starts is not true; the answer lists what.</summary>
    public const string PrerequisitesUnmet = "workflows.run.prerequisites_unmet.unprocessable";
}

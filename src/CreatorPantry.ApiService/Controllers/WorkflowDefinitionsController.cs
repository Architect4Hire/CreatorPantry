using Asp.Versioning;
using CreatorPantry.ApiService.Authorization;
using CreatorPantry.Domain.Modules.Workflows.Facade;
using CreatorPantry.Domain.Modules.Workflows.Managers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CreatorPantry.ApiService.Controllers;

/// <summary>FLOW-001: the guided workflows a workspace offers.</summary>
[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/workspaces/{workspaceSlug}/workflow-definitions")]
public sealed class WorkflowDefinitionsController(IWorkflowDefinitionFacade definitions) : ControllerBase
{
    /// <summary>Lists every guided workflow, and whether the caller can start it now.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and the
    /// caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Every member including a Viewer may read. One entry per workflow, at its newest version, in the order the
    /// product offers them: `goal`, `description` and `expectedOutcome` are plain language for the creator.
    ///
    /// **Whether a start would be accepted, asked in advance.** `canStart` is true when the workflow is built
    /// (`available`), nothing it needs first is missing (`waitingFor` is empty), and the caller can take its first
    /// step — a Contributor at least, and whatever more that step needs. When the caller's role is the only thing in
    /// the way, `neededRole` names the least role that could. Each `waitingFor` entry says what is missing in plain
    /// words and, in `route`, the page relative to the workspace where it is sorted out (`brand`, `recipes`), or null.
    /// A workflow not built yet has `available` false and its `unavailableReason`.
    ///
    /// `startsFrom` names the kinds of record a workflow can begin from elsewhere in the product; starting from none
    /// is always allowed. The answer is the same for every member of a role, holds no workspace record, and is
    /// `no-store` because whether a prerequisite is met is this workspace's.
    /// </remarks>
    [HttpGet]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceViewer)]
    [ProducesResponseType<IReadOnlyList<WorkflowDefinitionSummaryServiceModel>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(string workspaceSlug, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";

        return Ok(await definitions.ListAsync(cancellationToken));
    }
}

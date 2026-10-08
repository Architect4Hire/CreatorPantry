using Asp.Versioning;
using CreatorPantry.ApiService.Authorization;
using CreatorPantry.ApiService.Http;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Modules.Recipes.Facade;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.JsonWebTokens;

namespace CreatorPantry.ApiService.Controllers;

/// <summary>
/// The pictures attached to one recorded test of a recipe (RCPUB-005).
/// </summary>
[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/workspaces/{workspaceSlug}/recipes/{recipeId:guid}/test-runs/{testRunId:guid}/attachments")]
public sealed class RecipeTestAttachmentsController(IRecipeTestAttachmentFacade attachments) : ControllerBase
{
    /// <summary>Lists the pictures attached to one test, in order.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and the
    /// caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="recipeId">The recipe the test belongs to. Constrained to a Guid.</param>
    /// <param name="testRunId">The test whose pictures to read. Must be a test of that recipe.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Any member may read. Each item is a reference: the attachment's own `id`, the `mediaAssetId`, the pinned
    /// `mediaAssetVersionNumber` when there is one, and the `testIssueId` it illustrates when it illustrates
    /// one. **Never an address** — the bytes are read through the library's own routes, by asset and version.
    /// A test with no pictures answers an empty list. An unknown test, one on another recipe, and one in another
    /// workspace all answer `404 recipes.testRun.not_found`. The response is `no-store`.
    /// </remarks>
    [HttpGet]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceViewer)]
    [ProducesResponseType<IReadOnlyList<TestAttachmentServiceModel>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<IActionResult> List(
        string workspaceSlug, Guid recipeId, Guid testRunId, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";

        var result = await attachments.ListAsync(recipeId, testRunId, cancellationToken);

        return result.Succeeded ? Ok(result.Value) : this.ProblemFor(result.Error!);
    }

    /// <summary>Attaches one library asset to the test.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed; resolved server-side before the action runs (tenancy.md).
    /// </param>
    /// <param name="recipeId">The recipe the test belongs to. Constrained to a Guid.</param>
    /// <param name="testRunId">The test to attach to. Must be a test of that recipe.</param>
    /// <param name="model">The asset, and optionally a version to pin, an issue it illustrates, and a caption.</param>
    /// <param name="idempotencyKey">
    /// Optional. A repeat of the same key and the same request returns the original attachment with
    /// `Idempotent-Replayed: true`.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Contributor and above, the bar recording a test carries. **This is not an edit of the recipe or of what the
    /// test recorded**: no recipe version is written, and the test's own `concurrencyToken` stays valid.
    ///
    /// `versionNumber` **pins the attachment to one version of the asset**, which is usually what evidence of a
    /// trial wants; leave it out to follow whichever is current. `testIssueId` ties the picture to one issue of
    /// this same test. The new attachment goes to the end.
    ///
    /// **Only an asset in this workspace's library can be attached.** An unknown asset, another workspace's, and
    /// one removed from the library all answer `422 recipes.testAttachment.target.unprocessable` naming
    /// `mediaAssetId`, in the same words. The same code names `versionNumber` for a version the asset does not
    /// have and `testIssueId` for an issue that is not this test's.
    ///
    /// The same asset against the same issue (or against the test as a whole) twice answers
    /// `409 recipes.testAttachment.duplicate.conflict`. Two attachments saved at the same instant can collide on
    /// position: the loser answers `409 recipes.testAttachment.conflict` with nothing written, and should simply
    /// be sent again. An archived recipe answers `409 recipes.archived.conflict`. **Nothing about the asset
    /// changes**, and its usage history is not touched.
    /// </remarks>
    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceContributor)]
    [ProducesResponseType<TestAttachmentServiceModel>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")]
    public async Task<IActionResult> Attach(
        string workspaceSlug,
        Guid recipeId,
        Guid testRunId,
        AttachTestImageViewModel model,
        [FromHeader(Name = IdempotencyPolicy.KeyHeader)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirst(JwtRegisteredClaimNames.Sub)!.Value;
        var outcome = await attachments.AttachAsync(
            userId, recipeId, testRunId, model, idempotencyKey, cancellationToken);

        // 201 with no Location: an attachment has no read of its own to point at, and the collection it
        // belongs to is the GET on this same path.
        return this.IdempotentResult(outcome, created => StatusCode(StatusCodes.Status201Created, created));
    }

    /// <summary>Removes one picture from the test.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed; resolved server-side before the action runs (tenancy.md).
    /// </param>
    /// <param name="recipeId">The recipe the test belongs to. Constrained to a Guid.</param>
    /// <param name="testRunId">The test to detach from.</param>
    /// <param name="attachmentId">The attachment to remove — an `id` from this test's attachments, not an asset id.</param>
    /// <param name="idempotencyKey">
    /// Optional. A repeat of the same key returns the original response with `Idempotent-Replayed: true`.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Contributor and above. **Detaching removes the attachment and nothing else.** The asset stays in the
    /// library with every version and its usage history exactly as they were; no bytes are deleted. The response
    /// is the attachment that was removed.
    ///
    /// An id this test does not have answers `404 recipes.testAttachment.not_found` — the same answer for an id
    /// that names nothing, an attachment of another test, and one in another workspace. **Without an
    /// `Idempotency-Key`, detaching the same attachment twice answers that `404` the second time.**
    /// </remarks>
    [HttpDelete("{attachmentId:guid}")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceContributor)]
    [ProducesResponseType<TestAttachmentServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<IActionResult> Detach(
        string workspaceSlug,
        Guid recipeId,
        Guid testRunId,
        Guid attachmentId,
        [FromHeader(Name = IdempotencyPolicy.KeyHeader)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirst(JwtRegisteredClaimNames.Sub)!.Value;
        var outcome = await attachments.DetachAsync(
            userId, recipeId, testRunId, attachmentId, idempotencyKey, cancellationToken);

        return this.IdempotentResult(outcome, Ok);
    }
}

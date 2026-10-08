using Asp.Versioning;
using CreatorPantry.ApiService.Authorization;
using CreatorPantry.ApiService.Http;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Modules.Brand.Facade;
using CreatorPantry.Domain.Modules.Brand.Managers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.JsonWebTokens;

namespace CreatorPantry.ApiService.Controllers;

[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/workspaces/{workspaceSlug}/brand-profile")]
public sealed class BrandProfileController(IBrandProfileFacade brandProfile) : ControllerBase
{
    /// <summary>Returns the workspace's brand profile.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and the
    /// caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Each workspace has at most one profile. It holds brand facts only — name, description, default audience,
    /// default channels, locale, scheduling time zone, links and logo references — and never voice, tone or
    /// visual direction, which belong to the brand style guide. `concurrencyToken` identifies this state for the
    /// conditional write that follows in a later release; `revision` counts edits.
    ///
    /// A workspace that has not created a profile answers `404 brand.profile.not_found`, which a client treats
    /// as its first-use state. An unknown workspace and one the caller is not a member of both answer the
    /// tenancy 404 before this action runs, so neither is distinguishable from the other. Every member
    /// including a Viewer may read. The response is `no-store`: it is personalised and editable.
    /// </remarks>
    [HttpGet]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceViewer)]
    [ProducesResponseType<BrandProfileServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<IActionResult> Get(string workspaceSlug, CancellationToken cancellationToken)
    {
        var result = await brandProfile.GetAsync(cancellationToken);

        if (!result.Succeeded)
        {
            return this.ProblemFor(result.Error!);
        }

        Response.Headers.CacheControl = "no-store";

        return Ok(result.Value);
    }

    /// <summary>Creates the workspace's brand profile.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and the
    /// caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="model">
    /// The profile to create. Only `brandName` is required. Carries no workspace, owner, revision or audit
    /// field, and no voice, tone or visual direction, which belong to the brand style guide.
    /// </param>
    /// <param name="idempotencyKey">
    /// Optional. A repeat of the same key and the same body returns the original response with
    /// `Idempotent-Replayed: true` rather than failing on the profile it created.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Editor or above. A workspace has one profile: creating a second answers
    /// `409 brand.profile.exists.conflict`, and the way to change it is `PATCH`. The response is the profile
    /// as a read returns it, at `revision` 1.
    ///
    /// `assets` links logos from this workspace's library: at most one `PrimaryLogo`, each asset once. **Only an
    /// asset in this workspace's library can be linked.** An unknown asset, another workspace's and one removed
    /// from the library all answer `422 brand.assets.unprocessable` naming `assets[i].MediaAssetId`, in the same
    /// words. An empty or omitted list is accepted.
    /// </remarks>
    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceEditor)]
    [ProducesResponseType<BrandProfileServiceModel>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")]
    public async Task<IActionResult> Create(
        string workspaceSlug,
        CreateBrandProfileViewModel model,
        [FromHeader(Name = IdempotencyPolicy.KeyHeader)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirst(JwtRegisteredClaimNames.Sub)!.Value;
        var outcome = await brandProfile.CreateAsync(userId, model, idempotencyKey, cancellationToken);

        return this.IdempotentResult(outcome, created =>
            Created($"/api/v1/workspaces/{workspaceSlug}/brand-profile", created));
    }

    /// <summary>Changes the workspace's brand profile.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed; the workspace is resolved server-side (tenancy.md).
    /// </param>
    /// <param name="model">The merge patch and the concurrency token it was composed against.</param>
    /// <param name="idempotencyKey">Optional. A repeat of the same key and body returns the original response.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Editor or above. This is a JSON Merge Patch, not a replacement. A field the body does not mention is
    /// left exactly as it is; a field sent with a value is set; a field sent as `null` is cleared. Omitting a
    /// field never blanks it. `channelDefaults`, `links` and `assets` replace as a whole: a submitted list
    /// becomes the complete set, and `[]` or `null` empties it. `brandName` may be changed but not cleared.
    /// `expectedConcurrencyToken` is required and is not a profile field: it is the `concurrencyToken` from the
    /// read this edit was composed against, and an edit quoting a token the profile has moved past is refused
    /// with `409 brand.profile.conflict` rather than overwriting whoever saved first. `reason` is likewise not
    /// a profile field; it is recorded on the revision this edit writes. An edit that would change nothing
    /// writes no revision and leaves the token valid. The response is the whole profile as a read returns it,
    /// carrying the refreshed token the next edit must quote. A workspace with no profile answers
    /// `404 brand.profile.not_found`.
    ///
    /// A submitted `assets` list **replaces** the profile's logos; leaving `assets` out leaves them alone, and
    /// `[]` unlinks them all. **Unlinking removes the link and nothing else**: the asset stays in the library
    /// with every version and its usage history. A newly named asset that is not in this workspace's library
    /// answers `422 brand.assets.unprocessable` as `POST` does. A logo the profile already links is kept when
    /// re-submitted even if its asset has since been removed from the library, so an unrelated edit is never
    /// blocked by it.
    /// </remarks>
    [HttpPatch]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceEditor)]
    [ProducesResponseType<BrandProfileServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")]
    public async Task<IActionResult> Update(
        string workspaceSlug,
        UpdateBrandProfileViewModel model,
        [FromHeader(Name = IdempotencyPolicy.KeyHeader)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirst(JwtRegisteredClaimNames.Sub)!.Value;
        var outcome = await brandProfile.UpdateAsync(userId, model, idempotencyKey, cancellationToken);

        return this.IdempotentResult(outcome, Ok);
    }
}

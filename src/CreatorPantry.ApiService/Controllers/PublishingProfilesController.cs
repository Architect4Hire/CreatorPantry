using Asp.Versioning;
using CreatorPantry.ApiService.Authorization;
using CreatorPantry.Domain.Modules.Publishing.Facade;
using CreatorPantry.Domain.Modules.Publishing.Managers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CreatorPantry.ApiService.Controllers;

[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/workspaces/{workspaceSlug}/publishing/profiles")]
public sealed class PublishingProfilesController(IPublishingProfileFacade profiles) : ControllerBase
{
    /// <summary>Reads the places the workspace named by the route can publish to.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and the
    /// caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="refresh">
    /// `true` to ask the publishing provider now instead of serving a recent read. Left out, a read up to
    /// fifteen minutes old is served from storage. Honoured for a Contributor and above; ignored for a Viewer.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Every member including a Viewer may read. **This route always answers `200`.** What the publishing
    /// provider did is `connection.status`, and the profiles last read are listed whatever it says:
    ///
    /// - `Connected` — the provider answered; `profiles` is as it gave them.
    /// - `NotConfigured` — no provider is set up for this workspace. Not an error: planning, export and
    ///   download all still work. `connection.providerKey` is null.
    /// - `CredentialRejected` — the provider refused the configured key. An operator has to replace it.
    /// - `RateLimited` — the provider is throttling. `connection.retryAfterSeconds` says how long until it is
    ///   asked again.
    /// - `Unavailable` — the provider could not be reached or did not answer usefully.
    ///
    /// **A profile's `id` is CreatorPantry's own.** No provider identifier, credential, header or error text
    /// is ever returned: `providerKey` is a stable name such as `buffer`, and `platform` is CreatorPantry's
    /// vocabulary for the network — a network it does not know is `Other`. `timeZoneId` is an IANA zone or
    /// null. `status` is `Ready`, `NeedsReconnecting` (reconnect it at the provider) or `Locked`; a profile
    /// the provider no longer lists is left out.
    ///
    /// **The provider is asked sparingly**, because its quota is small. `refresh=true` asks again, but not
    /// more than once a minute and never while the provider is throttling; when it is not asked, the answer
    /// is the last one, and `connection.lastCheckedAt` says when that was. **A refresh takes the Contributor
    /// role.** A Viewer who sends it is not refused: they get the last read, exactly as without it.
    ///
    /// A provider key belongs to one workspace, named to the server by its id. **Every other workspace reads `NotConfigured`, exactly as on a
    /// deployment with no provider at all**, so this route cannot be used to learn that a neighbour has one.
    /// The response is `no-store`.
    /// </remarks>
    [HttpGet]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceViewer)]
    [ProducesResponseType<PublishingProfilesServiceModel>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        string workspaceSlug, [FromQuery] bool refresh = false, CancellationToken cancellationToken = default)
    {
        Response.Headers.CacheControl = "no-store";

        return Ok(await profiles.GetAsync(refresh, cancellationToken));
    }
}

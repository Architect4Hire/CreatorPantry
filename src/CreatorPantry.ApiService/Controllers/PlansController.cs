using Asp.Versioning;
using CreatorPantry.Domain.Modules.Billing.Facade;
using CreatorPantry.Domain.Modules.Billing.Managers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CreatorPantry.ApiService.Controllers;

/// <summary>
/// The published plan catalogue (LP.6): what a plan costs, what it includes, and the prepaid packs beside it.
/// </summary>
/// <remarks>
/// <para>
/// Anonymous, and the only anonymous route outside <c>auth</c>, because the public landing page reads it before
/// anyone has signed in. What makes that safe is the shape rather than the policy: the catalogue is
/// configuration the deployment publishes on purpose, with no account, workspace, usage or identity in it, so
/// there is nothing here to authorize against and nothing a caller could learn about anyone.
/// </para>
/// <para>
/// The same answer for every caller is also what makes it cacheable, so it carries a public <c>Cache-Control</c>
/// and the gateway rate-limits it at the edge.
/// </para>
/// </remarks>
[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/plans")]
public sealed class PlansController(IPlanCatalogueFacade planCatalogueFacade) : ControllerBase
{
    /// <summary>How long a shared cache may serve this without asking again.</summary>
    internal const int CacheSeconds = 300;

    /// <summary>
    /// The plans and packs on offer. A deployment that does not publish a catalogue answers with empty lists
    /// rather than a 404: an unpublished catalogue is a state, not a missing resource, and the page shows no
    /// pricing section either way.
    /// </summary>
    [HttpGet]
    [AllowAnonymous]
    [ProducesResponseType<PlanCatalogueServiceModel>(StatusCodes.Status200OK)]
    public IActionResult Catalogue()
    {
        var catalogue = planCatalogueFacade.Published();

        // After the answer, not before: a header set first would still be on the response if reading the
        // catalogue threw, and a shared cache would hold the failure for five minutes.
        Response.Headers.CacheControl = $"public, max-age={CacheSeconds}";

        return Ok(catalogue);
    }
}

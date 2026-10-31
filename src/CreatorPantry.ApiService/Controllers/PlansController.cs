using Asp.Versioning;
using CreatorPantry.ApiService.Http;
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

    /// <summary>
    /// What a month of <paramref name="pictures"/> and <paramref name="writing"/> comes to, and the smallest paid plan
    /// that carries it (LP.7, AP.7.2): the landing page's estimator, worked out here so the browser only formats it.
    /// </summary>
    /// <remarks>
    /// Anonymous and publicly cacheable for the reason the catalogue is: the answer is a function of the published
    /// configuration and the three query values, with nothing about any caller in it. The pictures are priced at
    /// `qualityClass` when the catalogue publishes it, else at the default class, else the cheapest; the class used
    /// comes back as `qualityClass`. Only paid plans are weighed, and one fits when its credits cover the month and
    /// its daily picture cap over a 30-day month covers the pictures. A count below 0 or above 100,000 answers
    /// `400 billing.estimate.invalid`; a deployment that publishes no catalogue answers
    /// `404 billing.estimate.not_found`.
    /// </remarks>
    /// <param name="pictures">Pictures a month.</param>
    /// <param name="writing">Recipe drafts and posts a month.</param>
    /// <param name="qualityClass">The picture quality to price at; omitted for the default.</param>
    [HttpGet("estimate")]
    [AllowAnonymous]
    [ProducesResponseType<PlanEstimateServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public IActionResult Estimate(
        [FromQuery] int pictures, [FromQuery] int writing, [FromQuery] string? qualityClass = null)
    {
        var result = planCatalogueFacade.Estimate(pictures, writing, qualityClass);

        if (!result.Succeeded)
        {
            return this.ProblemFor(result.Error!);
        }

        Response.Headers.CacheControl = $"public, max-age={CacheSeconds}";

        return Ok(result.Value);
    }
}

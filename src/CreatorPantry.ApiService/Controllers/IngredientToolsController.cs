using Asp.Versioning;
using CreatorPantry.ApiService.Authorization;
using CreatorPantry.ApiService.Http;
using CreatorPantry.Domain.Modules.Recipes.Facade;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CreatorPantry.ApiService.Controllers;

[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/workspaces/{workspaceSlug}/ingredient-tools")]
public sealed class IngredientToolsController(IIngredientParsingFacade parsingFacade) : ControllerBase
{
    /// <summary>
    /// Tokenizes and matches a batch of free-form recipe ingredient lines against the shared ingredient and
    /// unit catalogues. Read-only: nothing here is persisted, and no recipe is read or modified (ING-001).
    /// </summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and
    /// the caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="model">The lines to parse. Carries no workspace and no recipe id.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// A proposal only, at every stage: quantity/range and unit candidates never overwrite anything, and an
    /// unresolved or ambiguous match is returned as such rather than guessed at (recipes.md).
    /// </remarks>
    [HttpPost("parse")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceViewer)]
    [ProducesResponseType<ParseIngredientLinesResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    public async Task<IActionResult> Parse(
        string workspaceSlug,
        ParseIngredientLinesViewModel model,
        CancellationToken cancellationToken)
    {
        var result = await parsingFacade.ParseAsync(model, cancellationToken);

        return result.Succeeded ? Ok(result.Value) : this.ProblemFor(result.Error!);
    }
}

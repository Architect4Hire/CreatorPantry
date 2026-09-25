using Asp.Versioning;
using CreatorPantry.ApiService.Authorization;
using CreatorPantry.ApiService.Http;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Modules.Recipes.Facade;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.JsonWebTokens;

namespace CreatorPantry.ApiService.Controllers;

[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/workspaces/{workspaceSlug}/recipes")]
public sealed class RecipesController(IRecipeFacade recipeFacade) : ControllerBase
{
    /// <summary>Creates a recipe in the workspace named by the route.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace itself is resolved server-side from this
    /// segment and the caller's membership before the action runs; nothing here reads it, and no request
    /// field may name a workspace (tenancy.md).
    /// </param>
    /// <param name="model">The recipe to create. Carries no workspace, owner, version or audit field.</param>
    /// <param name="idempotencyKey">
    /// Optional. A repeat of the same key and the same recipe returns the original response with
    /// <c>Idempotent-Replayed: true</c> rather than creating a second recipe.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceContributor)]
    [ProducesResponseType<CreatedRecipeServiceModel>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")]
    public async Task<IActionResult> Create(
        string workspaceSlug,
        CreateRecipeViewModel model,
        [FromHeader(Name = IdempotencyPolicy.KeyHeader)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirst(JwtRegisteredClaimNames.Sub)!.Value;
        var outcome = await recipeFacade.CreateAsync(userId, model, idempotencyKey, cancellationToken);

        // No mapping beyond choosing the response: the facade already returns the ServiceModel, and the
        // location is the only thing this layer contributes, because only it knows the route shape.
        return this.IdempotentResult(outcome, created =>
            Created($"/api/v1/workspaces/{workspaceSlug}/recipes/{created.RecipeId}", created));
    }

    /// <summary>Lists the recipes of the workspace named by the route, filtered, ordered and paged.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and the
    /// caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="query">
    /// The filters, ordering, cursor and page size. Carries no workspace and no author id — see
    /// <see cref="RecipeSearchViewModel"/>.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Cursor-paged: follow `nextCursor` until it is null rather than comparing counts against a page size the
    /// server may have clamped. A cursor is bound to the workspace, ordering and filters it was issued for, so
    /// changing any of them means starting again without one — `recipes.cursor.invalid_request` says so
    /// explicitly. `limit` is clamped rather than refused. `totalCount` counts every match across all pages and
    /// is present unless `includeTotal=false`; it is a row count, not a page count, because a keyset ordering has
    /// no page N to jump to. Multi-valued filters are comma-separated. `sort` accepts only the two named
    /// orderings; there is no arbitrary sort column. An empty library and filters matching nothing are both an
    /// empty page rather than a 404.
    /// </remarks>
    [HttpGet]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceViewer)]
    [ProducesResponseType<RecipeSearchPageServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    public async Task<IActionResult> Search(
        string workspaceSlug,
        [FromQuery] RecipeSearchViewModel query,
        CancellationToken cancellationToken)
    {
        var result = await recipeFacade.SearchAsync(query, cancellationToken);

        return result.Succeeded ? Ok(result.Value) : this.ProblemFor(result.Error!);
    }

    /// <summary>Reads one recipe of the workspace named by the route.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and
    /// the caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="recipeId">
    /// The recipe to read. Constrained to a Guid, so a malformed id never reaches this action: routing answers
    /// 404, the same status as an unknown recipe and as one belonging to another workspace. The problem body
    /// differs — an edge 404 carries the generic <c>not_found</c> code rather than this module's — so nothing
    /// is disclosed, but a client branching on <c>code</c> sees two codes for what is one condition to it.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    [HttpGet("{recipeId:guid}")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceViewer)]
    [ProducesResponseType<RecipeDetailServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<IActionResult> Get(string workspaceSlug, Guid recipeId, CancellationToken cancellationToken)
    {
        var result = await recipeFacade.GetDetailAsync(recipeId, cancellationToken);

        return result.Succeeded ? Ok(result.Value) : this.ProblemFor(result.Error!);
    }

    /// <summary>Lists one recipe's versions, newest first.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and
    /// the caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="recipeId">
    /// The recipe whose history to read. Constrained to a Guid, so a malformed id never reaches this action —
    /// routing answers 404, the same status as an unknown recipe and as one belonging to another workspace.
    /// </param>
    /// <param name="query">The cursor and page size. Carries no workspace and no recipe.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Read-only: versions are immutable, and a correction to a recipe's history is a new version rather than
    /// a change to an old one. Metadata only — a version's content is never returned here, however many
    /// versions a recipe has. Cursor-paged: follow `nextCursor` until it is null. A cursor is bound to the
    /// workspace and recipe it was issued for, so replaying one against another recipe is refused with
    /// `recipes.cursor.invalid_request` rather than silently paging the wrong history. `limit` is clamped
    /// rather than refused. There is no total; follow the pages to count them. There is no `sort` — newest
    /// first is the contract. An unknown recipe and another workspace's recipe both answer
    /// `404 recipes.recipe.not_found`. A recipe created through this API has a version 1, so its history is
    /// normally non-empty — but an empty page is a legitimate answer and a client must render it rather than
    /// read `items[0]` unguarded.
    /// </remarks>
    [HttpGet("{recipeId:guid}/versions")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceViewer)]
    [ProducesResponseType<CursorPageServiceModel<RecipeVersionHistoryServiceModel>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<IActionResult> Versions(
        string workspaceSlug,
        Guid recipeId,
        [FromQuery] RecipeVersionHistoryViewModel query,
        CancellationToken cancellationToken)
    {
        var result = await recipeFacade.GetVersionHistoryAsync(recipeId, query, cancellationToken);

        return result.Succeeded ? Ok(result.Value) : this.ProblemFor(result.Error!);
    }

    /// <summary>Compares two of one recipe's versions and returns what differs between them.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and
    /// the caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="recipeId">
    /// The recipe whose versions to compare. Constrained to a Guid, so a malformed id never reaches this
    /// action — routing answers 404, the same status as an unknown recipe and as one belonging to another
    /// workspace.
    /// </param>
    /// <param name="query">Which two versions, by number. Carries no workspace and no recipe.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Read-only, and it writes nothing: comparing two versions leaves no record that it happened. The
    /// comparison is calculated on the server from the two archived documents and is the only one — a client
    /// renders it rather than deriving a second diff of its own, which is why the two snapshots are not
    /// returned. `from` and `to` are version numbers as the history lists them, not version ids: a number is
    /// unique only within its recipe, so a number naming another recipe's version matches nothing rather than
    /// being refused by a check. Either order is allowed, and `from` equal to `to` answers a comparison with
    /// no changes in it. An unknown recipe and another workspace's recipe both answer
    /// `404 recipes.recipe.not_found`; a version number this recipe does not have answers
    /// `404 recipes.version.not_found`, naming the parameter at fault. Section order is fixed and every
    /// section is present whether or not it changed, so a client renders a stable frame. Positions are ranks
    /// among siblings, not stored sort orders, and a reorder is reported as a move rather than as a
    /// replacement.
    /// </remarks>
    // A literal segment inside the versions namespace. A later GET .../versions/{versionId} must carry a
    // route constraint, or routing cannot tell that id from this word.
    [HttpGet("{recipeId:guid}/versions/compare")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceViewer)]
    [ProducesResponseType<RecipeVersionComparisonServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    // ValidationProblemDetails rather than ProblemDetails, unlike every other 404 on this controller: a
    // version number this recipe does not have names the parameter at fault in `errors`, and that naming is
    // the only reason this 404 is worth telling apart from recipes.recipe.not_found. A client cannot be asked
    // to depend on a field the contract does not promise.
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<IActionResult> CompareVersions(
        string workspaceSlug,
        Guid recipeId,
        [FromQuery] RecipeVersionComparisonViewModel query,
        CancellationToken cancellationToken)
    {
        var result = await recipeFacade.CompareVersionsAsync(recipeId, query, cancellationToken);

        return result.Succeeded ? Ok(result.Value) : this.ProblemFor(result.Error!);
    }

    /// <summary>Computes a deterministic scaling preview for one recipe of the workspace named by the route.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and
    /// the caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="recipeId">The recipe to scale. Constrained to a Guid, so a malformed id answers 404 at routing.</param>
    /// <param name="model">Which version to scale, and by a multiplier or a target yield.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Read-only (ING-003): nothing here is persisted, and the recipe is unchanged by having been scaled. The
    /// source version is required rather than defaulted, so the response's provenance is never ambiguous about
    /// what was scaled.
    /// </remarks>
    [HttpPost("{recipeId:guid}/calculations/scale")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceViewer)]
    [ProducesResponseType<RecipeScalingResultServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    // ValidationProblemDetails, like CompareVersions: an unknown version number names the field at fault, and
    // recipes.recipe.not_found (also possible here) is a plain ProblemDetails — both are documented as 404
    // since the response shape genuinely differs by which refusal was returned.
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<IActionResult> Scale(
        string workspaceSlug,
        Guid recipeId,
        ScaleRecipeViewModel model,
        CancellationToken cancellationToken)
    {
        var result = await recipeFacade.ScaleAsync(recipeId, model, cancellationToken);

        return result.Succeeded ? Ok(result.Value) : this.ProblemFor(result.Error!);
    }

    /// <summary>Computes a deterministic unit-conversion preview for one recipe of the workspace named by the route.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and
    /// the caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="recipeId">The recipe to read. Constrained to a Guid, so a malformed id answers 404 at routing.</param>
    /// <param name="model">Which version, the quantity to convert, and the units to convert between.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Read-only (ING-004): nothing here is persisted, and the recipe is unchanged by having a quantity
    /// converted. Same-dimension conversion only — a cross-dimension request answers 400 rather than
    /// inventing a density.
    /// </remarks>
    [HttpPost("{recipeId:guid}/calculations/convert-units")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceViewer)]
    [ProducesResponseType<RecipeUnitConversionResultServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<IActionResult> ConvertUnits(
        string workspaceSlug,
        Guid recipeId,
        ConvertUnitsViewModel model,
        CancellationToken cancellationToken)
    {
        var result = await recipeFacade.ConvertUnitsAsync(recipeId, model, cancellationToken);

        return result.Succeeded ? Ok(result.Value) : this.ProblemFor(result.Error!);
    }

    /// <summary>Computes a deterministic temperature-conversion preview for one recipe of the workspace named by the route.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and
    /// the caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="recipeId">The recipe to read. Constrained to a Guid, so a malformed id answers 404 at routing.</param>
    /// <param name="model">Which version, the structured value, and the scales to convert between.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Read-only (CALC-003): nothing here is persisted, and the recipe is unchanged by having a temperature
    /// converted. <c>value</c> must already be a structured number — this never infers one from heat language.
    /// </remarks>
    [HttpPost("{recipeId:guid}/calculations/convert-temperature")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceViewer)]
    [ProducesResponseType<RecipeTemperatureConversionResultServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<IActionResult> ConvertTemperature(
        string workspaceSlug,
        Guid recipeId,
        ConvertTemperatureViewModel model,
        CancellationToken cancellationToken)
    {
        var result = await recipeFacade.ConvertTemperatureAsync(recipeId, model, cancellationToken);

        return result.Succeeded ? Ok(result.Value) : this.ProblemFor(result.Error!);
    }

    /// <summary>Computes a deterministic yield-reconciliation preview for one recipe of the workspace named by the route.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and
    /// the caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="recipeId">The recipe to read. Constrained to a Guid, so a malformed id answers 404 at routing.</param>
    /// <param name="model">Which version, and the creator's explicit batch yield, serving count, serving size, and pan capacity.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Read-only (ING-005): nothing here is persisted, and the recipe is unchanged by having its yield
    /// recalculated. An unknown pan geometry or serving definition stays visibly unresolved rather than
    /// invented.
    /// </remarks>
    [HttpPost("{recipeId:guid}/calculations/recalculate-yield")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceViewer)]
    [ProducesResponseType<RecipeYieldReconciliationResultServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<IActionResult> RecalculateYield(
        string workspaceSlug,
        Guid recipeId,
        RecalculateYieldViewModel model,
        CancellationToken cancellationToken)
    {
        var result = await recipeFacade.RecalculateYieldAsync(recipeId, model, cancellationToken);

        return result.Succeeded ? Ok(result.Value) : this.ProblemFor(result.Error!);
    }

    /// <summary>Computes a deterministic display-normalization preview for one recipe of the workspace named by the route.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and
    /// the caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="recipeId">The recipe to read. Constrained to a Guid, so a malformed id answers 404 at routing.</param>
    /// <param name="model">Which version, the value (and optional range upper bound), the unit, and the presentation choices.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Read-only and presentation-only (ING-006): nothing here is persisted, and no canonical quantity or
    /// recipe version changes.
    /// </remarks>
    [HttpPost("{recipeId:guid}/calculations/normalize-display")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceViewer)]
    [ProducesResponseType<RecipeQuantityDisplayResultServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<IActionResult> NormalizeDisplay(
        string workspaceSlug,
        Guid recipeId,
        NormalizeDisplayViewModel model,
        CancellationToken cancellationToken)
    {
        var result = await recipeFacade.NormalizeDisplayAsync(recipeId, model, cancellationToken);

        return result.Succeeded ? Ok(result.Value) : this.ProblemFor(result.Error!);
    }

    /// <summary>Shelves one recipe: archives it without deleting anything.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and
    /// the caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="recipeId">The recipe to archive. Constrained to a Guid, so a malformed id answers 404 at routing.</param>
    /// <param name="model">The recipe's concurrency token. Carries nothing else.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// **Archiving is not deleting.** Every version, tag, media link, ingredient line and step stays exactly
    /// where it was, and the recipe is still readable by id — its history, its version comparisons and
    /// copying it all keep working. What changes is that it drops out of the default library listing
    /// (`GET .../recipes` returns it only for `?status=Archived`) and stops accepting content changes:
    /// `PATCH` and version restore both answer `409 recipes.archived.conflict` until it is brought back, and
    /// the AI proposal, publishing and test-run seams will do the same when they exist. Requires the Editor
    /// role, because archiving takes a recipe out of every collaborator's library rather than contributing to
    /// one. `expectedConcurrencyToken` is required: archiving a recipe someone else is editing should tell
    /// the archiver it moved under them rather than silently shelving work they have not seen. Archiving an
    /// already-archived recipe succeeds and changes nothing — no audit entry, no new token — so the command
    /// is safe to retry and takes no idempotency key. No version is written; the move is recorded in the
    /// audit log instead, because a version records what a recipe *said* and this changes none of that.
    /// </remarks>
    [HttpPost("{recipeId:guid}/archive")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceEditor)]
    [ProducesResponseType<RecipeDetailServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<IActionResult> Archive(
        string workspaceSlug,
        Guid recipeId,
        RecipeLifecycleViewModel model,
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirst(JwtRegisteredClaimNames.Sub)!.Value;
        var result = await recipeFacade.ArchiveAsync(userId, recipeId, model, cancellationToken);

        return result.Succeeded ? Ok(result.Value) : this.ProblemFor(result.Error!);
    }

    /// <summary>Brings one recipe back from the archive.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and
    /// the caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="recipeId">The recipe to bring back. Constrained to a Guid, so a malformed id answers 404 at routing.</param>
    /// <param name="model">The recipe's concurrency token. Carries nothing else.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// The mirror of the archive command, at the same Editor bar and with the same shape. **The recipe comes
    /// back as a `Draft`**, whatever it was before it was shelved — nothing records the earlier state, and
    /// `Ready` is a claim about a finished recipe that a creator picking one back up should make again
    /// themselves. Unarchiving a recipe that is not archived succeeds and changes nothing. Named
    /// `unarchive` rather than `restore` deliberately: `.../versions/{n}/restore` already means putting a
    /// recipe's *content* back, and one word for two operations would be a trap in a client and in this
    /// codebase alike.
    /// </remarks>
    [HttpPost("{recipeId:guid}/unarchive")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceEditor)]
    [ProducesResponseType<RecipeDetailServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<IActionResult> Unarchive(
        string workspaceSlug,
        Guid recipeId,
        RecipeLifecycleViewModel model,
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirst(JwtRegisteredClaimNames.Sub)!.Value;
        var result = await recipeFacade.UnarchiveAsync(userId, recipeId, model, cancellationToken);

        return result.Succeeded ? Ok(result.Value) : this.ProblemFor(result.Error!);
    }

    /// <summary>Copies one recipe into a new, independent recipe of its own.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and
    /// the caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="recipeId">The recipe to copy from. Constrained to a Guid, so a malformed id answers 404 at routing.</param>
    /// <param name="model">
    /// The copy's title, and optionally which version to copy. Carries no workspace, owner, version or audit
    /// field, and no content — everything but the title comes from the source.
    /// </param>
    /// <param name="idempotencyKey">
    /// Optional, and worth sending: a repeat of the same key, recipe, version and title returns the original
    /// response with <c>Idempotent-Replayed: true</c> rather than leaving the creator with two copies to tell
    /// apart.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// The copy is a recipe in its own right, not a derivative: editing either recipe does nothing to the
    /// other, and the copy has its own history starting at version 1, whose `source` is `Duplicate` and whose
    /// recipe records `duplicatedFromVersionId`. Requires the Contributor role — the same bar as creating a
    /// recipe, and deliberately not the Editor bar a restore carries, because copying takes nothing away from
    /// anyone. `sourceVersionNumber` is optional: omitting it copies the recipe as it currently stands, and a
    /// number is a version number as the history lists it rather than a version id. `title` is required, so a
    /// copy is never momentarily indistinguishable from its source; nothing else may be sent, and editing the
    /// copy afterwards is an ordinary `PATCH`. **The copy is always a `Draft`**, whatever the source's status
    /// — including when the source was archived. Everything the archive holds is copied, including
    /// ingredients, method, equipment, tags and media links, with fresh ids throughout; media links point at
    /// the same assets, and no asset is copied or re-owned. The source's version history, its concurrency
    /// token, its authorship and its timestamps are not copied. There is **no `expectedConcurrencyToken` and
    /// no `409`** — nothing is being overwritten, which makes this the one recipe write with no state to quote.
    /// An unknown recipe and another workspace's recipe both answer `404 recipes.recipe.not_found`; a version
    /// number the source does not have answers `404 recipes.version.not_found`, naming `sourceVersionNumber`.
    /// </remarks>
    [HttpPost("{recipeId:guid}/duplicate")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceContributor)]
    [ProducesResponseType<CreatedRecipeServiceModel>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    // ValidationProblemDetails, as on the comparison and restore routes: an unknown source version names
    // `sourceVersionNumber` in `errors`, and that naming is the only reason this 404 is worth telling apart
    // from recipes.recipe.not_found.
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")]
    public async Task<IActionResult> Duplicate(
        string workspaceSlug,
        Guid recipeId,
        DuplicateRecipeViewModel model,
        [FromHeader(Name = IdempotencyPolicy.KeyHeader)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirst(JwtRegisteredClaimNames.Sub)!.Value;
        var outcome = await recipeFacade.DuplicateAsync(userId, recipeId, model, idempotencyKey, cancellationToken);

        // 201 and a Location, exactly as a create: the resource this produced is a new recipe, and the
        // location is the only thing this layer contributes because only it knows the route shape. It points
        // at the copy, never at the recipe in the route.
        return this.IdempotentResult(outcome, created =>
            Created($"/api/v1/workspaces/{workspaceSlug}/recipes/{created.RecipeId}", created));
    }

    /// <summary>Puts one recipe back to what one of its versions said, as a new current version.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and
    /// the caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="recipeId">The recipe to restore. Constrained to a Guid, so a malformed id answers 404 at routing.</param>
    /// <param name="versionNumber">
    /// Which version's content to put back, as the history lists it — a version number, not a version id.
    /// Constrained to an integer of at least 1, which is both what keeps this segment distinguishable from the
    /// literal <c>compare</c> above and what makes a number no version could carry a routing 404 rather than a
    /// lookup.
    /// </param>
    /// <param name="model">
    /// The recipe's concurrency token and an optional reason. Carries no content: everything restored comes
    /// from the archive.
    /// </param>
    /// <param name="idempotencyKey">
    /// Optional. A repeat of the same key, recipe, version and token returns the original response with
    /// <c>Idempotent-Replayed: true</c> rather than writing a second version.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Restoring never rewrites history. The version named here is read and left exactly where it is — it does
    /// not become current again, is not renumbered and is not deleted — and the recipe's content is put back as
    /// a *new* version whose parent is the version it replaced and whose `restoredFromVersionId` names the one
    /// it came from. Requires the Editor role, a step above editing, because one request discards every change
    /// made since the chosen version without naming them. `expectedConcurrencyToken` is required and is the
    /// `concurrencyToken` from the read this restore was decided against; one the recipe has moved past is
    /// refused with `409 recipes.recipe.conflict` rather than overwriting whoever saved in between, and nothing
    /// of the creator's is lost by reloading and asking again. `reason` is not a recipe field; it is recorded
    /// on the version this writes. The whole recipe is restored — its own fields including `status`, so
    /// restoring a version captured before a recipe was archived un-archives it, along with its ingredients,
    /// method, equipment, media links and tags. A restore that would change nothing writes no version and
    /// leaves the token valid, so a resubmission is safe. An unknown recipe and another workspace's recipe both
    /// answer `404 recipes.recipe.not_found`; a version number this recipe does not have answers
    /// `404 recipes.version.not_found`, naming `versionNumber`. The response is the whole recipe, in the same
    /// shape a read returns, carrying the refreshed token the next write must quote.
    /// </remarks>
    [HttpPost("{recipeId:guid}/versions/{versionNumber:int:min(1)}/restore")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceEditor)]
    [ProducesResponseType<RecipeDetailServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    // ValidationProblemDetails, as on the comparison route and for the same reason: an unknown version number
    // names `versionNumber` in `errors`, and that naming is the only thing worth telling this 404 apart from
    // recipes.recipe.not_found for. A client cannot be asked to depend on a field the contract omits.
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")]
    public async Task<IActionResult> RestoreVersion(
        string workspaceSlug,
        Guid recipeId,
        int versionNumber,
        RestoreRecipeVersionViewModel model,
        [FromHeader(Name = IdempotencyPolicy.KeyHeader)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirst(JwtRegisteredClaimNames.Sub)!.Value;
        var outcome = await recipeFacade.RestoreVersionAsync(
            userId, recipeId, versionNumber, model, idempotencyKey, cancellationToken);

        // 200, not 201: the restore creates a version, but the resource this route acts on is the recipe, and
        // the recipe is where it always was. A Location header would have to point at either the recipe the
        // caller already knows or a version resource this API does not serve.
        return this.IdempotentResult(outcome, Ok);
    }

    /// <summary>Changes part of one recipe and returns it as it now stands.</summary>
    /// <param name="workspaceSlug">
    /// Bound only so the route is well formed. The workspace is resolved server-side from this segment and
    /// the caller's membership before the action runs, and nothing here reads it (tenancy.md).
    /// </param>
    /// <param name="recipeId">The recipe to change. Constrained to a Guid, so a malformed id answers 404 at routing.</param>
    /// <param name="model">
    /// The fields to change. Fields the body does not mention are left alone, and a field sent as
    /// <c>null</c> is cleared — see <see cref="UpdateRecipeViewModel"/>. The body carries the recipe's
    /// concurrency token and no workspace, owner, version or audit field.
    /// </param>
    /// <param name="idempotencyKey">
    /// Optional. A repeat of the same key and the same edit returns the original response with
    /// <c>Idempotent-Replayed: true</c> rather than being answered as a conflict.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// This is a JSON Merge Patch, not a replacement. A field the body does not mention is left exactly as
    /// it is; a field sent with a value is set to it; a field sent as `null` is cleared. Omitting a field
    /// never blanks it. `tags` is the one field that replaces rather than merges — a submitted list becomes
    /// the recipe's complete set, and `[]` or `null` removes them all. `title` and `status` may be changed
    /// but not cleared. `expectedConcurrencyToken` is required and is not a field of the recipe: it is the
    /// `concurrencyToken` from the read this edit was composed against, and an edit quoting a token the
    /// recipe has moved past is refused with `409 recipes.recipe.conflict` rather than overwriting whoever
    /// saved first. `reason` is likewise not a recipe field; it is recorded on the version this edit writes.
    /// An edit that would change nothing writes no version and leaves the token valid. The response is the
    /// whole recipe, in the same shape a read returns, so an editor can rebind from it: it carries the
    /// refreshed token the next edit must quote, and the version this one wrote.
    /// </remarks>
    [HttpPatch("{recipeId:guid}")]
    [Authorize(Policy = AuthorizationPolicies.WorkspaceContributor)]
    [ProducesResponseType<RecipeDetailServiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")]
    public async Task<IActionResult> Update(
        string workspaceSlug,
        Guid recipeId,
        UpdateRecipeViewModel model,
        [FromHeader(Name = IdempotencyPolicy.KeyHeader)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirst(JwtRegisteredClaimNames.Sub)!.Value;
        var outcome = await recipeFacade.UpdateAsync(userId, recipeId, model, idempotencyKey, cancellationToken);

        // No location to contribute and no mapping to do: the facade already returns the ServiceModel, and
        // the recipe is where it always was.
        return this.IdempotentResult(outcome, Ok);
    }
}

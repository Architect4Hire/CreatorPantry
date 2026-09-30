using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Recipes.Business;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Recipes.Facade;

/// <summary>
/// The application boundary for moving a recipe between editorial states (TESTRUN-005).
/// </summary>
/// <remarks>
/// <para>
/// <strong>A facade of its own for the reason <see cref="IRecipeReadinessFacade"/> is one.</strong> The
/// approval needs a readiness evaluation, which reaches the Ai and Ingredients modules;
/// <c>AiProposalBusiness</c> already depends on <see cref="IRecipeFacade"/>, so putting this on that
/// interface would close a constructor cycle through the container and fail at resolution rather than at
/// review. Two dependencies running in opposite directions between different types resolve without
/// difficulty.
/// </para>
/// <para>
/// <strong>The archive and restore routes deliberately do not come through here.</strong> They keep the
/// facade they have had since REC-006, because their own contract has not changed — what changed is that the
/// Business method underneath them is now the machine. Moving them would break two shipped routes to no
/// purpose.
/// </para>
/// </remarks>
public interface IRecipeStatusTransitionFacade
{
    /// <summary>
    /// Moves one recipe to another editorial state.
    /// </summary>
    /// <param name="actorUserId">The authenticated caller, for the audit entry. Never from a request field.</param>
    /// <param name="recipeId">The recipe to move, from the route.</param>
    /// <param name="model">The target, the reason and the token the request was composed against.</param>
    /// <param name="idempotencyKey">
    /// <strong>Required</strong>, unlike every other command in this module. A caller who loses the response to
    /// an approval cannot tell whether the recipe was approved, and the blind retry that follows is the one
    /// request that must not be able to write a second time. Missing, it is refused with
    /// <c>idempotency.key_required</c>.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>
    /// The recipe as it now stands, or a refusal: <c>recipes.recipe.not_found</c> for a recipe the caller may
    /// not see, <c>recipes.transition.invalid_request</c> for a move the machine does not have or a reopen with
    /// no reason, <c>recipes.transition.forbidden</c> for a move above the caller's role,
    /// <c>recipes.transition.blocked.conflict</c> for an approval over outstanding blockers, and
    /// <c>recipes.recipe.conflict</c> for a token the recipe has moved past.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong>The readiness evaluation is the server's own.</strong> A caller cannot hand in a verdict; when
    /// the target needs one this asks for it, and Business then refuses it if it describes content that has
    /// since changed. That is what "a fresh readiness evaluation" means here.
    /// </para>
    /// <para>
    /// <strong>Two replay paths, and they answer differently on purpose.</strong> The same key replays the
    /// original response. A fresh key asking for the state the recipe already holds is a new request that
    /// Business answers as a no-op success — because it is one, and because that is the behaviour REC-006
    /// documented for archiving an archived recipe.
    /// </para>
    /// </remarks>
    Task<IdempotentOutcome<RecipeDetailServiceModel>> TransitionAsync(
        string actorUserId,
        Guid recipeId,
        RecipeReadinessTransitionViewModel model,
        string? idempotencyKey,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="IRecipeStatusTransitionFacade"/>
internal sealed class RecipeStatusTransitionFacade(
    IValidator<RecipeReadinessTransitionViewModel> validator,
    IRecipeBusiness business,
    IRecipeReadinessFacade readiness,
    IWorkspaceContext workspace,
    IIdempotentCommandExecutor idempotency) : IRecipeStatusTransitionFacade
{
    /// <summary>Stable operation name for the idempotency scope. Changing it orphans in-flight keys.</summary>
    private const string TransitionOperation = "recipes.readinessTransitions.create";

    private const string CannotTransition = "That recipe could not be moved as described.";

    public async Task<IdempotentOutcome<RecipeDetailServiceModel>> TransitionAsync(
        string actorUserId,
        Guid recipeId,
        RecipeReadinessTransitionViewModel model,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        // Authorization first, so a caller who may make no move at all learns that rather than which of their
        // fields is invalid. The floor, not the bar for this particular move: which role a move needs depends
        // on where the recipe is, which Business establishes. Checked here and not only at the controller
        // policy because this boundary is also reached by workers and AI plugins, which no MVC policy protects.
        if (workspace.Role < WorkspaceRole.Contributor)
        {
            return Refused(
                RecipeErrorCodes.TransitionForbidden,
                "You do not have permission to move recipes through review in this workspace.");
        }

        var validation = await validator.ValidateAsync(model, cancellationToken);
        if (!validation.IsValid)
        {
            return Refused(OperationError.Validation(
                RecipeErrorCodes.TransitionInvalidRequest,
                CannotTransition,
                validation.Errors.Select(failure => (failure.PropertyName, failure.ErrorMessage))));
        }

        var canonical = CanonicalRecipeTransition.From(model);

        // Decided from the target alone, which is knowable without reading anything: the machine's only gated
        // move is the approval, and asking which rule applies would mean loading the recipe here to learn its
        // current state — work Business is about to do properly, with a concurrency check around it.
        var gated = RecipeStatusTransitions.All.Any(
            rule => rule.To == canonical.Target && rule.RequiresReadinessClear);

        return await idempotency.ExecuteAsync(
            new IdempotentCommand(
                actorUserId,
                workspace.WorkspaceId,
                TransitionOperation,
                idempotencyKey,
                Fingerprint: canonical.Fingerprint(recipeId),

                // Required, unlike every other command here. A lost response to an approval leaves the creator
                // unable to tell whether the recipe was approved, and the blind retry that follows is exactly
                // what must not write a second version — so the key stops being a courtesy and becomes part of
                // the contract. See the interface's own remarks.
                KeyRequired: true),

            // The evaluation is made *inside* the idempotent body, which is what keeps a replay free: the
            // executor returns the committed answer without running this at all, so a retry costs neither the
            // readiness read nor the two cross-module reads behind it. It also puts the gate next to the write
            // it gates, rather than a step before it.
            async token =>
            {
                RecipeReadinessServiceModel? evaluation = null;

                if (gated)
                {
                    var read = await readiness.EvaluateAsync(recipeId, token);

                    if (!read.Succeeded)
                    {
                        // Its own refusal, which for an invisible recipe is the 404 this route must answer
                        // with anyway — an unknown recipe and another workspace's answered identically
                        // (tenancy.md). A failed body commits no key, so the caller may retry with the same
                        // one once the recipe is visible to them.
                        return OperationResult<RecipeDetailServiceModel>.Failure(read.Error!);
                    }

                    evaluation = read.Value;
                }

                // A target that turns out not to be reachable from the recipe's state is refused here, so an
                // evaluation is occasionally made for a transition that is then rejected. That is the cheaper
                // mistake: the alternative is two loads of the recipe, or a Business API in two halves holding
                // a tracked aggregate between them.
                return await business.TransitionAsync(
                    recipeId,
                    canonical.Target,
                    canonical.Reason,
                    evaluation,
                    actorUserId,
                    canonical.ExpectedConcurrencyToken,
                    token);
            },
            cancellationToken);
    }

    private static IdempotentOutcome<RecipeDetailServiceModel> Refused(string code, string message) =>
        Refused(new OperationError(code, message, new Dictionary<string, string[]>()));

    private static IdempotentOutcome<RecipeDetailServiceModel> Refused(OperationError error) =>
        new(OperationResult<RecipeDetailServiceModel>.Failure(error), Replayed: false);
}

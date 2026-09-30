using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Recipes.Business;
using CreatorPantry.Domain.Modules.Recipes.Managers;

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
    /// <param name="recipeId">The recipe to move, from the route.</param>
    /// <param name="target">The state to move it to.</param>
    /// <param name="reason">Why, in the caller's own words. Required for a reopen.</param>
    /// <param name="actorUserId">The authenticated caller, for the audit entry. Never from a request field.</param>
    /// <param name="expectedConcurrencyToken">The state the command was composed against.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// <para>
    /// Domain-shaped rather than taking a ViewModel, because there is no route yet: 10.7a adds
    /// <c>POST .../recipes/{recipeId}/readiness-transitions</c> with the input shape, its validator and its
    /// idempotency key, and maps onto this. Keeping HTTP out of here means the machine can be exercised —
    /// and was — before any of that exists.
    /// </para>
    /// <para>
    /// <strong>The readiness evaluation is the server's own.</strong> A caller cannot hand in a verdict; when
    /// the target needs one this asks for it, and Business then refuses it if it describes content that has
    /// since changed. That is what "a fresh readiness evaluation" means here.
    /// </para>
    /// </remarks>
    Task<OperationResult<RecipeDetailServiceModel>> TransitionAsync(
        Guid recipeId,
        RecipeStatus target,
        string? reason,
        string actorUserId,
        string? expectedConcurrencyToken,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="IRecipeStatusTransitionFacade"/>
internal sealed class RecipeStatusTransitionFacade(
    IRecipeBusiness business,
    IRecipeReadinessFacade readiness) : IRecipeStatusTransitionFacade
{
    public async Task<OperationResult<RecipeDetailServiceModel>> TransitionAsync(
        Guid recipeId,
        RecipeStatus target,
        string? reason,
        string actorUserId,
        string? expectedConcurrencyToken,
        CancellationToken cancellationToken)
    {
        RecipeReadinessServiceModel? evaluation = null;

        // Decided from the target alone, which is knowable without reading anything: the machine's only gated
        // move is the approval, and asking which rule applies would mean loading the recipe here to learn its
        // current state — work Business is about to do properly, with a concurrency check around it.
        //
        // A target that turns out not to be reachable from the recipe's state is refused by Business
        // afterwards, so an evaluation is occasionally made for a transition that is then rejected. That is
        // the cheaper mistake: the alternative is two loads of the recipe, or a Business API in two halves
        // holding a tracked aggregate between them.
        if (RecipeStatusTransitions.All.Any(rule => rule.To == target && rule.RequiresReadinessClear))
        {
            var read = await readiness.EvaluateAsync(recipeId, cancellationToken);

            if (!read.Succeeded)
            {
                // Its own refusal, which for an invisible recipe is the 404 this route must answer with
                // anyway — an unknown recipe and another workspace's answered identically (tenancy.md).
                return OperationResult<RecipeDetailServiceModel>.Failure(read.Error!);
            }

            evaluation = read.Value;
        }

        return await business.TransitionAsync(
            recipeId,
            target,
            reason,
            evaluation,
            actorUserId,
            expectedConcurrencyToken,
            cancellationToken);
    }
}

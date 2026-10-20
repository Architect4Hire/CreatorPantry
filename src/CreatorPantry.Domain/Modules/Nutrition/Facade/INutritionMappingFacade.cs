using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Nutrition.Business;
using CreatorPantry.Domain.Modules.Nutrition.Managers;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Nutrition.Facade;

/// <summary>
/// A workspace's nutrition mappings: which external reference each platform ingredient is, as its creators
/// decided (14.2). The boundary controllers, workers and plugins use; no route serves it yet (14.6b).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Roles, checked here because workers and plugins reach this without an MVC policy.</strong> Any member
/// may read. A Contributor may ask for a proposal, which only ever writes a candidate or an unresolved mapping
/// nobody decided — never over a creator's confirmation or "none fits". Confirming, rejecting, remapping and
/// marking unresolved decide what a published estimate rests on, so they take an Editor, the role that approves
/// a recipe; so does a refresh, because it can withdraw a confirmed reference and the portion an Editor set.
/// </para>
/// <para>
/// The workspace is the resolved context's, never a parameter: every read and write is filtered to it, and
/// another workspace's mapping is not found.
/// </para>
/// </remarks>
public interface INutritionMappingFacade
{
    Task<OperationResult<NutritionMappingServiceModel>> GetAsync(Guid ingredientId, CancellationToken cancellationToken);

    /// <summary>This workspace's mappings among the ingredients named — what an estimate (14.3) will read.</summary>
    /// <exception cref="ArgumentOutOfRangeException">More than <see cref="NutritionMappingPolicy.MaxListIngredients"/> ids.</exception>
    Task<IReadOnlyList<NutritionMappingServiceModel>> ListAsync(
        IReadOnlyCollection<Guid> ingredientIds, CancellationToken cancellationToken);

    /// <inheritdoc cref="INutritionMappingBusiness.ProposeAsync"/>
    Task<OperationResult<NutritionMappingServiceModel>> ProposeAsync(Guid ingredientId, CancellationToken cancellationToken);

    /// <inheritdoc cref="INutritionMappingBusiness.ConfirmAsync"/>
    Task<OperationResult<NutritionMappingServiceModel>> ConfirmAsync(
        Guid ingredientId, ConfirmNutritionMappingViewModel model, CancellationToken cancellationToken);

    /// <inheritdoc cref="INutritionMappingBusiness.RejectAsync"/>
    Task<OperationResult<NutritionMappingServiceModel>> RejectAsync(
        Guid ingredientId, NutritionMappingDecisionViewModel model, CancellationToken cancellationToken);

    /// <inheritdoc cref="INutritionMappingBusiness.RemapAsync"/>
    Task<OperationResult<NutritionMappingServiceModel>> RemapAsync(
        Guid ingredientId, RemapNutritionMappingViewModel model, CancellationToken cancellationToken);

    /// <inheritdoc cref="INutritionMappingBusiness.MarkUnresolvedAsync"/>
    Task<OperationResult<NutritionMappingServiceModel>> MarkUnresolvedAsync(
        Guid ingredientId, NutritionMappingDecisionViewModel model, CancellationToken cancellationToken);

    /// <inheritdoc cref="INutritionMappingBusiness.RefreshAsync"/>
    Task<OperationResult<NutritionMappingServiceModel>> RefreshAsync(Guid ingredientId, CancellationToken cancellationToken);
}

internal sealed class NutritionMappingFacade(
    INutritionMappingBusiness business,
    IWorkspaceContext workspace,
    IValidator<ConfirmNutritionMappingViewModel> confirmValidator,
    IValidator<RemapNutritionMappingViewModel> remapValidator,
    IValidator<NutritionMappingDecisionViewModel> decisionValidator) : INutritionMappingFacade
{
    public Task<OperationResult<NutritionMappingServiceModel>> GetAsync(Guid ingredientId, CancellationToken cancellationToken) =>
        business.GetAsync(ingredientId, cancellationToken);

    public Task<IReadOnlyList<NutritionMappingServiceModel>> ListAsync(
        IReadOnlyCollection<Guid> ingredientIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ingredientIds);

        // A recipe's distinct ingredients, not a catalogue: past this, the IN list is a mistake, not a recipe.
        ArgumentOutOfRangeException.ThrowIfGreaterThan(ingredientIds.Count, NutritionMappingPolicy.MaxListIngredients);

        return business.ListAsync(ingredientIds, cancellationToken);
    }

    public Task<OperationResult<NutritionMappingServiceModel>> ProposeAsync(Guid ingredientId, CancellationToken cancellationToken) =>
        workspace.Role < WorkspaceRole.Contributor
            ? Forbidden("You do not have permission to change nutrition mappings in this workspace.")
            : business.ProposeAsync(ingredientId, cancellationToken);

    public Task<OperationResult<NutritionMappingServiceModel>> RefreshAsync(Guid ingredientId, CancellationToken cancellationToken) =>
        workspace.Role < WorkspaceRole.Editor
            ? ForbiddenDecision()
            : business.RefreshAsync(ingredientId, cancellationToken);

    public async Task<OperationResult<NutritionMappingServiceModel>> ConfirmAsync(
        Guid ingredientId, ConfirmNutritionMappingViewModel model, CancellationToken cancellationToken)
    {
        if (workspace.Role < WorkspaceRole.Editor)
        {
            return await ForbiddenDecision();
        }

        if (await InvalidAsync(confirmValidator, model, cancellationToken) is { } invalid)
        {
            return invalid;
        }

        return await business.ConfirmAsync(ingredientId, model, cancellationToken);
    }

    public async Task<OperationResult<NutritionMappingServiceModel>> RejectAsync(
        Guid ingredientId, NutritionMappingDecisionViewModel model, CancellationToken cancellationToken)
    {
        if (workspace.Role < WorkspaceRole.Editor)
        {
            return await ForbiddenDecision();
        }

        if (await InvalidAsync(decisionValidator, model, cancellationToken) is { } invalid)
        {
            return invalid;
        }

        return await business.RejectAsync(ingredientId, model, cancellationToken);
    }

    public async Task<OperationResult<NutritionMappingServiceModel>> RemapAsync(
        Guid ingredientId, RemapNutritionMappingViewModel model, CancellationToken cancellationToken)
    {
        if (workspace.Role < WorkspaceRole.Editor)
        {
            return await ForbiddenDecision();
        }

        if (await InvalidAsync(remapValidator, model, cancellationToken) is { } invalid)
        {
            return invalid;
        }

        return await business.RemapAsync(ingredientId, model, cancellationToken);
    }

    public async Task<OperationResult<NutritionMappingServiceModel>> MarkUnresolvedAsync(
        Guid ingredientId, NutritionMappingDecisionViewModel model, CancellationToken cancellationToken)
    {
        if (workspace.Role < WorkspaceRole.Editor)
        {
            return await ForbiddenDecision();
        }

        if (await InvalidAsync(decisionValidator, model, cancellationToken) is { } invalid)
        {
            return invalid;
        }

        return await business.MarkUnresolvedAsync(ingredientId, model, cancellationToken);
    }

    private static async Task<OperationResult<NutritionMappingServiceModel>?> InvalidAsync<TModel>(
        IValidator<TModel> validator, TModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        var validation = await validator.ValidateAsync(model, cancellationToken);

        return validation.IsValid
            ? null
            : OperationResult<NutritionMappingServiceModel>.Failure(OperationError.Validation(
                NutritionErrorCodes.MappingInvalid,
                "That change could not be read as described.",
                validation.Errors.Select(error => (error.PropertyName, error.ErrorMessage))));
    }

    private static Task<OperationResult<NutritionMappingServiceModel>> ForbiddenDecision() =>
        Forbidden("Deciding or refreshing nutrition references takes an Editor in this workspace.");

    private static Task<OperationResult<NutritionMappingServiceModel>> Forbidden(string message) =>
        Task.FromResult(OperationResult<NutritionMappingServiceModel>.Failure(
            new OperationError(NutritionErrorCodes.MappingForbidden, message, new Dictionary<string, string[]>())));
}

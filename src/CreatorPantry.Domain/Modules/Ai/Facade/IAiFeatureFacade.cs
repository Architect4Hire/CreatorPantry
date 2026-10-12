using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Ai.Business;
using CreatorPantry.Domain.Modules.Ai.Managers;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Ai.Facade;

/// <summary>
/// The application boundary for a workspace's AI features: which are switched on, and changing that.
/// </summary>
public interface IAiFeatureFacade
{
    /// <summary>Every switchable feature and whether it is on. Any member may read.</summary>
    Task<OperationResult<AiFeaturesServiceModel>> GetAsync(CancellationToken cancellationToken);

    /// <summary>Replaces the set of features that are on. Owner only.</summary>
    Task<OperationResult<AiFeaturesServiceModel>> ReplaceAsync(
        ReplaceAiFeaturesViewModel model, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IAiFeatureFacade"/>
internal sealed class AiFeatureFacade(
    IAiFeatureBusiness business,
    IValidator<ReplaceAiFeaturesViewModel> validator,
    IWorkspaceContext workspace) : IAiFeatureFacade
{
    public Task<OperationResult<AiFeaturesServiceModel>> GetAsync(CancellationToken cancellationToken) =>
        business.GetAsync(cancellationToken);

    public async Task<OperationResult<AiFeaturesServiceModel>> ReplaceAsync(
        ReplaceAiFeaturesViewModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        // The route policy has already refused a lower role; this is the backstop for a worker or plugin
        // reaching the facade directly, which is the whole reason the facade rather than the controller owns
        // the bar. Owner, because switching a feature on is a decision to spend the account's allowance on it.
        if (workspace.Role < WorkspaceRole.Owner)
        {
            return OperationResult<AiFeaturesServiceModel>.Failure(new OperationError(
                AiFeatureErrors.Forbidden,
                "Only an Owner of this workspace can change which AI features are switched on.",
                new Dictionary<string, string[]>()));
        }

        var validation = await validator.ValidateAsync(model, cancellationToken);

        if (!validation.IsValid)
        {
            return OperationResult<AiFeaturesServiceModel>.Failure(OperationError.Validation(
                AiFeatureErrors.Invalid,
                "The AI features could not be saved.",
                validation.Errors.Select(failure => (failure.PropertyName, failure.ErrorMessage))));
        }

        return await business.ReplaceAsync(model, cancellationToken);
    }
}

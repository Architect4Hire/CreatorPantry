using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Content.Business;
using CreatorPantry.Domain.Modules.Content.Managers;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Content.Facade;

/// <summary>
/// The application boundary for the content-seed generator: one idea, from reference vocabulary and the
/// workspace's own week.
/// </summary>
/// <remarks>
/// No idempotency and no authorization bar of its own. Nothing is written, so there is nothing to make idempotent
/// and nothing a replay could duplicate; and the route policy's Viewer bar is the whole rule, because this
/// composes only data every member may already read. A facade with nothing to coordinate is still the boundary —
/// it owns validation, and it is what a plugin or worker would call.
/// </remarks>
public interface IContentSeedFacade
{
    /// <inheritdoc cref="IContentSeedBusiness.GenerateAsync"/>
    Task<OperationResult<ContentSeedServiceModel>> GenerateAsync(
        ContentSeedQueryViewModel model, CancellationToken cancellationToken);
}

internal sealed class ContentSeedFacade(
    IValidator<ContentSeedQueryViewModel> validator,
    IContentSeedBusiness business) : IContentSeedFacade
{
    public async Task<OperationResult<ContentSeedServiceModel>> GenerateAsync(
        ContentSeedQueryViewModel model, CancellationToken cancellationToken)
    {
        var validation = await validator.ValidateAsync(model, cancellationToken);
        if (!validation.IsValid)
        {
            return OperationResult<ContentSeedServiceModel>.Failure(OperationError.Validation(
                ContentErrorCodes.ContentSeedInvalid,
                "A content seed could not be generated.",
                validation.Errors.Select(failure => (failure.PropertyName, failure.ErrorMessage))));
        }

        return await business.GenerateAsync(model, cancellationToken);
    }
}

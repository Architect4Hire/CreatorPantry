using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Ai.Business;
using CreatorPantry.Domain.Modules.Ai.Managers;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Ai.Facade;

/// <summary>The application boundary for trying a brand guide version out (11A.24).</summary>
public interface IAiBrandStyleTestDriveFacade
{
    Task<IdempotentOutcome<BrandStyleTestDriveServiceModel>> RequestAsync(
        RequestBrandStyleTestDriveViewModel model, string? idempotencyKey, CancellationToken cancellationToken);

    Task<OperationResult<BrandStyleTestDriveServiceModel>> GetAsync(
        Guid requestId, CancellationToken cancellationToken);
}

internal sealed class AiBrandStyleTestDriveFacade(
    IAiBrandStyleTestDriveBusiness business,
    IValidator<RequestBrandStyleTestDriveViewModel> validator) : IAiBrandStyleTestDriveFacade
{
    public async Task<IdempotentOutcome<BrandStyleTestDriveServiceModel>> RequestAsync(
        RequestBrandStyleTestDriveViewModel model, string? idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        var validation = await validator.ValidateAsync(model, cancellationToken);

        if (!validation.IsValid)
        {
            return new IdempotentOutcome<BrandStyleTestDriveServiceModel>(
                OperationResult<BrandStyleTestDriveServiceModel>.Failure(OperationError.Validation(
                    AiBrandStyleTestDriveRequestErrors.RequestInvalid,
                    "The request is not valid.",
                    validation.Errors.Select(error => (error.PropertyName, error.ErrorMessage)))),
                Replayed: false);
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return new IdempotentOutcome<BrandStyleTestDriveServiceModel>(
                OperationResult<BrandStyleTestDriveServiceModel>.Failure(new OperationError(
                    IdempotencyPolicy.KeyRequiredCode,
                    $"Supply an {IdempotencyPolicy.KeyHeader} header. A test drive is two generations against "
                        + "your allowance, so a retried request must return the first answer rather than buy a "
                        + "second pair.",
                    new Dictionary<string, string[]>())),
                Replayed: false);
        }

        return await business.RequestAsync(model, idempotencyKey, cancellationToken);
    }

    public Task<OperationResult<BrandStyleTestDriveServiceModel>> GetAsync(
        Guid requestId, CancellationToken cancellationToken) =>
        business.GetAsync(requestId, cancellationToken);
}

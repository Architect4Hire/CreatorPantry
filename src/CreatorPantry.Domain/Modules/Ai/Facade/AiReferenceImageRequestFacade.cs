using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Ai.Business;
using CreatorPantry.Domain.Modules.Ai.Managers;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Ai.Facade;

/// <summary>
/// The application boundary for requesting and reading IMG-004 reference image analyses.
/// </summary>
/// <remarks>
/// A workspace-level route: the subject is an uploaded photograph, and no recipe is named at all, so there is
/// no recipe segment for a nested route to supply.
/// </remarks>
public interface IAiReferenceImageRequestFacade
{
    /// <summary>
    /// Queues the reading of one reference image. Long work: this returns as soon as the request is recorded,
    /// and the model is called by the worker afterwards.
    /// </summary>
    /// <param name="idempotencyKey">
    /// Required. A vision call is the most expensive kind this product makes, so a retried request must
    /// return the first answer rather than buy a second reading.
    /// </param>
    Task<IdempotentOutcome<AiProposalStatusServiceModel>> RequestAsync(
        RequestReferenceImageAnalysisViewModel model,
        string? idempotencyKey,
        CancellationToken cancellationToken);

    /// <summary>Where a requested reading has got to, and the observations and prompt once there are some.</summary>
    Task<OperationResult<AiProposalStatusServiceModel>> GetAsync(
        Guid requestId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IAiReferenceImageRequestFacade"/>
internal sealed class AiReferenceImageRequestFacade(
    IAiReferenceImageRequestBusiness business,
    IValidator<RequestReferenceImageAnalysisViewModel> validator) : IAiReferenceImageRequestFacade
{
    public async Task<IdempotentOutcome<AiProposalStatusServiceModel>> RequestAsync(
        RequestReferenceImageAnalysisViewModel model,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        var validation = await validator.ValidateAsync(model, cancellationToken);

        if (!validation.IsValid)
        {
            return new IdempotentOutcome<AiProposalStatusServiceModel>(
                OperationResult<AiProposalStatusServiceModel>.Failure(OperationError.Validation(
                    AiReferenceImageRequestErrors.RequestInvalid,
                    "The request is not valid.",
                    validation.Errors.Select(error => (error.PropertyName, error.ErrorMessage)))),
                Replayed: false);
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return new IdempotentOutcome<AiProposalStatusServiceModel>(
                OperationResult<AiProposalStatusServiceModel>.Failure(new OperationError(
                    IdempotencyPolicy.KeyRequiredCode,
                    $"Supply an {IdempotencyPolicy.KeyHeader} header. Reading an image is not free, so a "
                        + "retried request must be able to return the first answer rather than buy a second.",
                    new Dictionary<string, string[]>())),
                Replayed: false);
        }

        return await business.RequestAsync(model, idempotencyKey, cancellationToken);
    }

    public Task<OperationResult<AiProposalStatusServiceModel>> GetAsync(
        Guid requestId, CancellationToken cancellationToken) =>
        business.GetAsync(requestId, cancellationToken);
}

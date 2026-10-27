using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Ai.Business;
using CreatorPantry.Domain.Modules.Ai.Managers;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Ai.Facade;

/// <summary>
/// The application boundary for asking for a shorter version of an image prompt (AP.3.2), for a picture quality
/// whose model takes fewer characters than the prompt and its avoid list come to.
/// </summary>
/// <remarks>
/// Read back through <see cref="IAiImagePromptRequestFacade.GetAsync"/>: a shorter prompt is an image-prompt
/// request like any other, with the same status resource and the same proposal shape.
/// </remarks>
public interface IAiImagePromptCompactionRequestFacade
{
    /// <summary>
    /// Queues a shorter version of one prompt. Long work: this returns as soon as the request is recorded, and
    /// the model is called by the worker afterwards.
    /// </summary>
    /// <param name="idempotencyKey">Required, for the reason composing a prompt requires one.</param>
    Task<IdempotentOutcome<AiProposalStatusServiceModel>> RequestAsync(
        RequestImagePromptCompactionViewModel model,
        string? idempotencyKey,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="IAiImagePromptCompactionRequestFacade"/>
internal sealed class AiImagePromptCompactionRequestFacade(
    IAiImagePromptCompactionRequestBusiness business,
    IValidator<RequestImagePromptCompactionViewModel> validator) : IAiImagePromptCompactionRequestFacade
{
    public async Task<IdempotentOutcome<AiProposalStatusServiceModel>> RequestAsync(
        RequestImagePromptCompactionViewModel model,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        var validation = await validator.ValidateAsync(model, cancellationToken);

        if (!validation.IsValid)
        {
            return new IdempotentOutcome<AiProposalStatusServiceModel>(
                OperationResult<AiProposalStatusServiceModel>.Failure(OperationError.Validation(
                    AiImagePromptCompactionErrors.RequestInvalid,
                    "The request is not valid.",
                    validation.Errors.Select(error => (error.PropertyName, error.ErrorMessage)))),
                Replayed: false);
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return new IdempotentOutcome<AiProposalStatusServiceModel>(
                OperationResult<AiProposalStatusServiceModel>.Failure(new OperationError(
                    IdempotencyPolicy.KeyRequiredCode,
                    $"Supply an {IdempotencyPolicy.KeyHeader} header. Shortening a prompt is not free, so a "
                        + "retried request must be able to return the first answer rather than buy a second.",
                    new Dictionary<string, string[]>())),
                Replayed: false);
        }

        return await business.RequestAsync(model, idempotencyKey, cancellationToken);
    }
}

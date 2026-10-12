using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Content.Business;
using CreatorPantry.Domain.Modules.Content.Managers;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Content.Facade;

/// <summary>
/// A long-form draft read whole, and its acceptance (LF.3.4). Every call is made under the workspace filter.
/// </summary>
public interface ILongFormDocumentFacade
{
    /// <inheritdoc cref="ILongFormDocumentBusiness.GetDocumentAsync" />
    Task<OperationResult<LongFormAssembledDocumentServiceModel>> GetDocumentAsync(Guid draftId, CancellationToken cancellationToken);

    /// <inheritdoc cref="ILongFormDocumentBusiness.AcceptAsync" />
    Task<OperationResult<LongFormAssembledDocumentServiceModel>> AcceptAsync(
        string actorUserId, Guid draftId, AcceptLongFormDocumentViewModel model, CancellationToken cancellationToken);
}

internal sealed class LongFormDocumentFacade(
    ILongFormDocumentBusiness business,
    IValidator<AcceptLongFormDocumentViewModel> acceptValidator) : ILongFormDocumentFacade
{
    public Task<OperationResult<LongFormAssembledDocumentServiceModel>> GetDocumentAsync(
        Guid draftId, CancellationToken cancellationToken) =>
        business.GetDocumentAsync(draftId, cancellationToken);

    public async Task<OperationResult<LongFormAssembledDocumentServiceModel>> AcceptAsync(
        string actorUserId, Guid draftId, AcceptLongFormDocumentViewModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        var validation = await acceptValidator.ValidateAsync(model, cancellationToken);

        return validation.IsValid
            ? await business.AcceptAsync(actorUserId, draftId, model, cancellationToken)
            : OperationResult<LongFormAssembledDocumentServiceModel>.Failure(OperationError.Validation(
                ContentErrorCodes.LongFormAcceptanceInvalid,
                "The request is not valid.",
                validation.Errors.Select(error => (error.PropertyName, error.ErrorMessage))));
    }
}

using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Ai.Business;
using CreatorPantry.Domain.Modules.Ai.Managers;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Ai.Facade;

/// <summary>The application boundary for requesting posts for a piece of creative work (AF.6.4).</summary>
public interface IAiChannelPostsRequestFacade
{
    /// <inheritdoc cref="IAiChannelPostsRequestBusiness.RequestAsync"/>
    /// <remarks>Contributor and above: a request spends the account's allowance.</remarks>
    Task<IdempotentOutcome<AiProposalStatusServiceModel>> RequestAsync(
        RequestChannelPostsViewModel model, string? idempotencyKey, CancellationToken cancellationToken);

    /// <inheritdoc cref="IAiChannelPostsRequestBusiness.GetAsync"/>
    /// <remarks>Any member.</remarks>
    Task<OperationResult<ChannelPostRequestStatusServiceModel>> GetAsync(
        Guid requestId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IAiChannelPostsRequestFacade"/>
internal sealed class AiChannelPostsRequestFacade(
    IAiChannelPostsRequestBusiness business,
    IValidator<RequestChannelPostsViewModel> validator) : IAiChannelPostsRequestFacade
{
    public async Task<IdempotentOutcome<AiProposalStatusServiceModel>> RequestAsync(
        RequestChannelPostsViewModel model, string? idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        var validation = await validator.ValidateAsync(model, cancellationToken);

        if (!validation.IsValid)
        {
            return Refuse(OperationError.Validation(
                AiChannelPostsRequestErrors.RequestInvalid,
                "The request is not valid.",
                validation.Errors.Select(error => (error.PropertyName, error.ErrorMessage))));
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return Refuse(new OperationError(
                IdempotencyPolicy.KeyRequiredCode,
                $"Supply an {IdempotencyPolicy.KeyHeader} header. Generation is not free, so a retried "
                    + "request must be able to return the first answer rather than buy a second set of posts.",
                new Dictionary<string, string[]>()));
        }

        return await business.RequestAsync(model, idempotencyKey, cancellationToken);
    }

    public Task<OperationResult<ChannelPostRequestStatusServiceModel>> GetAsync(
        Guid requestId, CancellationToken cancellationToken) =>
        business.GetAsync(requestId, cancellationToken);

    private static IdempotentOutcome<AiProposalStatusServiceModel> Refuse(OperationError error) =>
        new(OperationResult<AiProposalStatusServiceModel>.Failure(error), Replayed: false);
}

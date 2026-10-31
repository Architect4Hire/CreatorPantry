using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.AiUsage.Business;
using CreatorPantry.Domain.Modules.AiUsage.Managers;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.AiUsage.Facade;

/// <summary>
/// What the signed-in account does with its own low-credit notice (PRICE-011 as amended, B-59).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The account is a parameter and never a request field.</strong> The caller passes the id from the
/// validated session; no route segment, body field, query value or header in this feature can carry one,
/// which is what makes "acts on the caller's own account only" structural rather than checked.
/// </para>
/// <para>
/// Both operations are idempotent, so a repeated click, a retried request and a replayed one all leave the
/// same state behind and none of them is an error.
/// </para>
/// </remarks>
public interface IAiLowCreditNoticeFacade
{
    /// <summary>
    /// Puts away this period's notice. Does nothing, successfully, when there is none standing.
    /// </summary>
    /// <param name="accountId">From the validated session.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    Task DismissAsync(string accountId, CancellationToken cancellationToken);

    /// <summary>
    /// Records whether this account wants to be told when its credits run low. On until it says otherwise.
    /// </summary>
    /// <param name="accountId">From the validated session.</param>
    /// <param name="model">Whether to raise the notice for this account. Saying nothing is refused.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    Task<OperationResult<bool>> SetEnabledAsync(
        string accountId, LowCreditNoticeViewModel model, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IAiLowCreditNoticeFacade"/>
internal sealed class AiLowCreditNoticeFacade(
    IAiLowCreditNoticeBusiness business,
    IValidator<LowCreditNoticeViewModel> validator) : IAiLowCreditNoticeFacade
{
    public Task DismissAsync(string accountId, CancellationToken cancellationToken) =>
        business.DismissAsync(accountId, cancellationToken);

    public async Task<OperationResult<bool>> SetEnabledAsync(
        string accountId, LowCreditNoticeViewModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        var validation = await validator.ValidateAsync(model, cancellationToken);

        if (!validation.IsValid)
        {
            return OperationResult<bool>.Failure(OperationError.Validation(
                AiLowCreditNoticeErrors.SettingInvalid,
                "The low-credit notice setting is not valid.",
                validation.Errors.Select(error => (error.PropertyName, error.ErrorMessage))));
        }

        await business.SetEnabledAsync(accountId, model.Enabled!.Value, cancellationToken);

        return OperationResult<bool>.Success(true);
    }
}

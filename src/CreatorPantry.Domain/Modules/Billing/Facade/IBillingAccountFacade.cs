using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Billing.Business;
using CreatorPantry.Domain.Modules.Billing.Managers;
using FluentValidation;
using FluentValidation.Results;

namespace CreatorPantry.Domain.Modules.Billing.Facade;

/// <summary>
/// The signed-in account's own billing (AP.6.5, B-57): its page, and the Checkout, portal and plan-change commands
/// behind the page's buttons.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Keyed by the account the caller established</strong> — the session's subject on a <c>/me</c> route — and
/// never by a workspace, a body or a query (B-47, USAGE-008). A plan and a pack belong to the person.
/// </para>
/// <para>
/// <strong>Every command is idempotent</strong> on its <c>Idempotency-Key</c>, scoped to the account and the operation:
/// a retried click returns the provider page the first one made, rather than a second Checkout.
/// </para>
/// </remarks>
public interface IBillingAccountFacade
{
    Task<BillingAccountServiceModel> GetAsync(string accountId, CancellationToken cancellationToken);

    Task<IdempotentOutcome<BillingRedirectServiceModel>> StartPlanCheckoutAsync(
        string accountId, StartPlanCheckoutViewModel model, string? idempotencyKey, CancellationToken cancellationToken);

    Task<IdempotentOutcome<BillingRedirectServiceModel>> StartPackCheckoutAsync(
        string accountId, StartPackCheckoutViewModel model, string? idempotencyKey, CancellationToken cancellationToken);

    Task<IdempotentOutcome<BillingRedirectServiceModel>> ChangePlanAsync(
        string accountId, ChangePlanViewModel model, string? idempotencyKey, CancellationToken cancellationToken);

    Task<IdempotentOutcome<bool>> WithdrawPlanChangeAsync(
        string accountId, string? idempotencyKey, CancellationToken cancellationToken);

    Task<OperationResult<BillingRedirectServiceModel>> OpenPortalAsync(
        string accountId, OpenBillingPortalViewModel model, CancellationToken cancellationToken);

    Task<OperationResult<BillingCheckoutStatusServiceModel>> GetCheckoutAsync(
        string accountId, BillingCheckoutKind kind, string? externalSessionId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IBillingAccountFacade"/>
internal sealed class BillingAccountFacade(
    IBillingAccountBusiness business,
    IIdempotentCommandExecutor idempotency,
    IValidator<StartPlanCheckoutViewModel> planValidator,
    IValidator<StartPackCheckoutViewModel> packValidator,
    IValidator<ChangePlanViewModel> changeValidator,
    IValidator<OpenBillingPortalViewModel> portalValidator) : IBillingAccountFacade
{
    private const string PlanCheckoutOperation = "billing.checkout.plan";
    private const string PackCheckoutOperation = "billing.checkout.pack";
    private const string ChangeOperation = "billing.plan.change";
    private const string WithdrawOperation = "billing.plan.withdraw";

    public Task<BillingAccountServiceModel> GetAsync(string accountId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);

        return business.GetAsync(accountId, cancellationToken);
    }

    public async Task<IdempotentOutcome<BillingRedirectServiceModel>> StartPlanCheckoutAsync(
        string accountId, StartPlanCheckoutViewModel model, string? idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentNullException.ThrowIfNull(model);

        if (await planValidator.ValidateAsync(model, cancellationToken) is { IsValid: false } invalid)
        {
            return Refused<BillingRedirectServiceModel>(invalid);
        }

        return await idempotency.ExecuteAsync(
            new IdempotentCommand(
                accountId, WorkspaceId: null, PlanCheckoutOperation, idempotencyKey,
                new { model.PlanKey, model.Cycle, model.ReturnOrigin }),
            token => business.StartPlanCheckoutAsync(
                accountId, model.PlanKey!, model.Cycle!.Value, model.ReturnOrigin, idempotencyKey!, token),
            cancellationToken);
    }

    public async Task<IdempotentOutcome<BillingRedirectServiceModel>> StartPackCheckoutAsync(
        string accountId, StartPackCheckoutViewModel model, string? idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentNullException.ThrowIfNull(model);

        if (await packValidator.ValidateAsync(model, cancellationToken) is { IsValid: false } invalid)
        {
            return Refused<BillingRedirectServiceModel>(invalid);
        }

        return await idempotency.ExecuteAsync(
            new IdempotentCommand(
                accountId, WorkspaceId: null, PackCheckoutOperation, idempotencyKey,
                new { model.PackKey, model.ReturnOrigin }),
            token => business.StartPackCheckoutAsync(accountId, model.PackKey!, model.ReturnOrigin, idempotencyKey!, token),
            cancellationToken);
    }

    public async Task<IdempotentOutcome<BillingRedirectServiceModel>> ChangePlanAsync(
        string accountId, ChangePlanViewModel model, string? idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentNullException.ThrowIfNull(model);

        if (await changeValidator.ValidateAsync(model, cancellationToken) is { IsValid: false } invalid)
        {
            return Refused<BillingRedirectServiceModel>(invalid);
        }

        return await idempotency.ExecuteAsync(
            new IdempotentCommand(
                accountId, WorkspaceId: null, ChangeOperation, idempotencyKey,
                new { model.PlanKey, model.Cycle, model.ReturnOrigin }),
            token => business.ChangePlanAsync(
                accountId, model.PlanKey!, model.Cycle!.Value, model.ReturnOrigin, idempotencyKey!, token),
            cancellationToken);
    }

    public Task<IdempotentOutcome<bool>> WithdrawPlanChangeAsync(
        string accountId, string? idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);

        return idempotency.ExecuteAsync(
            new IdempotentCommand(accountId, WorkspaceId: null, WithdrawOperation, idempotencyKey, new { }),
            token => business.WithdrawPlanChangeAsync(accountId, idempotencyKey!, token),
            cancellationToken);
    }

    /// <remarks>
    /// Not idempotent by key: opening the portal changes nothing, and each opening is a short-lived session of its own.
    /// </remarks>
    public async Task<OperationResult<BillingRedirectServiceModel>> OpenPortalAsync(
        string accountId, OpenBillingPortalViewModel model, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentNullException.ThrowIfNull(model);

        if (await portalValidator.ValidateAsync(model, cancellationToken) is { IsValid: false } invalid)
        {
            return OperationResult<BillingRedirectServiceModel>.Failure(Invalid(invalid));
        }

        return await business.OpenPortalAsync(accountId, model.ReturnOrigin, cancellationToken);
    }

    public async Task<OperationResult<BillingCheckoutStatusServiceModel>> GetCheckoutAsync(
        string accountId, BillingCheckoutKind kind, string? externalSessionId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);

        if (kind is not (BillingCheckoutKind.Plan or BillingCheckoutKind.Pack)
            || string.IsNullOrWhiteSpace(externalSessionId)
            || externalSessionId.Length > BillingAccountPolicy.SessionIdMaxLength)
        {
            return OperationResult<BillingCheckoutStatusServiceModel>.Failure(OperationError.Validation(
                BillingAccountErrors.Invalid,
                "That checkout could not be looked up.",
                [("sessionId", "A checkout is named by the session it came back with, and what it was for.")]));
        }

        return OperationResult<BillingCheckoutStatusServiceModel>.Success(
            await business.GetCheckoutAsync(accountId, kind, externalSessionId, cancellationToken));
    }

    private static IdempotentOutcome<T> Refused<T>(ValidationResult invalid) =>
        new(OperationResult<T>.Failure(Invalid(invalid)), Replayed: false);

    private static OperationError Invalid(ValidationResult invalid) =>
        OperationError.Validation(
            BillingAccountErrors.Invalid,
            "That request could not be read.",
            invalid.Errors.Select(error => (error.PropertyName, error.ErrorMessage)));
}

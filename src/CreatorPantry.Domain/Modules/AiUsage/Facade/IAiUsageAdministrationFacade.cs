using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.AiUsage.Business;
using CreatorPantry.Domain.Modules.AiUsage.Managers;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.AiUsage.Facade;

/// <summary>
/// Platform administration of any account's AI allowance (USAGE-009): read it, set or clear its quota,
/// suspend or restore its access, and see who is consuming the platform.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Separate from <see cref="IAiUsageReadFacade"/> on purpose.</strong> That facade's contract is that
/// the account is "never a route segment, a body field, a query value or a header" — the structural guarantee
/// behind <c>GET /api/v1/me/ai-usage</c> returning the caller's own account and nothing else. An ops route
/// legitimately names another account, so it gets its own seam rather than weakening that one until it no
/// longer says anything.
/// </para>
/// <para>
/// <strong>Reached only through <c>/api/v1/ops/*</c> behind the ops policy</strong> (baseline B-14): a hashed,
/// rotatable machine key, never a browser session, and never a workspace role. A Workspace Owner is not a
/// platform administrator, and <c>PlatformAdmin</c> grants no workspace membership in the other direction.
/// </para>
/// <para>
/// <strong>Numbers, accounts and workspace identifiers only.</strong> Nothing this facade returns carries a
/// recipe, a prompt, a proposal, a workspace name, or any other creator content — the tables behind it hold
/// none, which is what makes an administrator's cross-account read safe rather than careful.
/// </para>
/// </remarks>
public interface IAiUsageAdministrationFacade
{
    /// <param name="accountId">From the route. An administrator names the account they are asking about.</param>
    Task<OperationResult<AccountAiUsageAdminServiceModel>> GetAccountAsync(
        string accountId, CancellationToken cancellationToken);

    /// <summary>The highest-consuming accounts between <paramref name="from"/> and <paramref name="to"/>.</summary>
    Task<OperationResult<AiUsageTopConsumersServiceModel>> GetTopConsumersAsync(
        DateTimeOffset from, DateTimeOffset to, int limit, CancellationToken cancellationToken);

    /// <summary>Puts explicit terms in force for the account, replacing any it already had.</summary>
    /// <param name="actor">The authenticated ops client. Never a request field.</param>
    Task<OperationResult<AccountAiQuotaChangeServiceModel>> SetQuotaAsync(
        string accountId, SetAccountAiQuotaViewModel model, PlatformActor actor,
        CancellationToken cancellationToken);

    /// <summary>Returns the account to the configured platform default in every term.</summary>
    /// <inheritdoc cref="SetQuotaAsync" path="/param[@name='actor']"/>
    Task<OperationResult<AccountAiQuotaChangeServiceModel>> ClearQuotaAsync(
        string accountId, AccountAiQuotaReasonViewModel model, PlatformActor actor,
        CancellationToken cancellationToken);

    /// <summary>Switches the account's AI access off or back on. Takes effect on the next request.</summary>
    /// <inheritdoc cref="SetQuotaAsync" path="/param[@name='actor']"/>
    Task<OperationResult<AccountAiQuotaChangeServiceModel>> SetSuspensionAsync(
        string accountId, bool suspended, AccountAiQuotaReasonViewModel model, PlatformActor actor,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="IAiUsageAdministrationFacade"/>
/// <remarks>
/// Shape validation happens here, as it does in every other facade: the validators bound the reason and the
/// zone identifier, and Business decides the combinations the quota table's check constraints forbid, because
/// only it knows the configured defaults a null allowance falls back to.
/// </remarks>
internal sealed class AiUsageAdministrationFacade(
    IValidator<SetAccountAiQuotaViewModel> quotaValidator,
    IValidator<AccountAiQuotaReasonViewModel> reasonValidator,
    IAiUsageAdministrationBusiness business) : IAiUsageAdministrationFacade
{
    public Task<OperationResult<AccountAiUsageAdminServiceModel>> GetAccountAsync(
        string accountId, CancellationToken cancellationToken) =>
        business.GetAccountAsync(accountId, cancellationToken);

    public Task<OperationResult<AiUsageTopConsumersServiceModel>> GetTopConsumersAsync(
        DateTimeOffset from, DateTimeOffset to, int limit, CancellationToken cancellationToken) =>
        business.GetTopConsumersAsync(from, to, limit, cancellationToken);

    public async Task<OperationResult<AccountAiQuotaChangeServiceModel>> SetQuotaAsync(
        string accountId, SetAccountAiQuotaViewModel model, PlatformActor actor,
        CancellationToken cancellationToken) =>
        await ValidateAsync(quotaValidator, model, cancellationToken)
        ?? await business.SetQuotaAsync(
            accountId,
            new AccountAiQuotaTermsInput(
                model.Unit,
                model.Allowance,
                model.PeriodLength,
                model.PeriodAnchor,
                (model.TimeZoneId ?? string.Empty).Trim(),
                model.CarryOver,
                model.CarryOverCap),
            model.Reason.Trim(),
            actor,
            cancellationToken);

    public async Task<OperationResult<AccountAiQuotaChangeServiceModel>> ClearQuotaAsync(
        string accountId, AccountAiQuotaReasonViewModel model, PlatformActor actor,
        CancellationToken cancellationToken) =>
        await ValidateAsync(reasonValidator, model, cancellationToken)
        ?? await business.ClearQuotaAsync(accountId, model.Reason.Trim(), actor, cancellationToken);

    public async Task<OperationResult<AccountAiQuotaChangeServiceModel>> SetSuspensionAsync(
        string accountId, bool suspended, AccountAiQuotaReasonViewModel model, PlatformActor actor,
        CancellationToken cancellationToken) =>
        await ValidateAsync(reasonValidator, model, cancellationToken)
        ?? await business.SetSuspensionAsync(accountId, suspended, model.Reason.Trim(), actor, cancellationToken);

    private static async Task<OperationResult<AccountAiQuotaChangeServiceModel>?> ValidateAsync<TModel>(
        IValidator<TModel> validator, TModel model, CancellationToken cancellationToken)
    {
        var validation = await validator.ValidateAsync(model, cancellationToken);

        return validation.IsValid
            ? null
            : OperationResult<AccountAiQuotaChangeServiceModel>.Failure(OperationError.Validation(
                AiUsageAdministrationErrors.QuotaInvalid,
                "The request is invalid.",
                validation.Errors.Select(error => (error.PropertyName, error.ErrorMessage))));
    }
}

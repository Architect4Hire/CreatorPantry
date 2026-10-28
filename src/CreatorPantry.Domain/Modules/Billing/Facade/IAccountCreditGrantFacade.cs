using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Billing.Business;
using CreatorPantry.Domain.Modules.Billing.Managers;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Billing.Facade;

/// <summary>
/// The application boundary for credits granted to an account beyond its allowance (PRICE-008, AP.5.1): grant
/// them by hand, list them, and claw back what is unspent — each audited.
/// </summary>
/// <remarks>
/// <strong>Keyed by account, never by workspace</strong> (B-47), as <see cref="IAccountPlanFacade"/> is. Every
/// operation here is an administrator's, named by the account in an <c>ops</c> route and carrying the
/// <see cref="PlatformActor"/> resolved from the authenticated ops credential; nothing takes an account from a
/// request body, a query or a model, and a creator cannot reach any of it (B-14).
/// </remarks>
public interface IAccountCreditGrantFacade
{
    /// <summary>Grants an account credits by hand, with a platform audit row (AP.5.1).</summary>
    /// <param name="idempotencyKey">
    /// Required. A grant is a new row every time, so without one an operator's double submit would grant twice;
    /// the same key and body answers with the first grant instead.
    /// </param>
    Task<IdempotentOutcome<AccountCreditGrantChangeServiceModel>> GrantAsync(
        string accountId,
        GrantAccountCreditsViewModel model,
        PlatformActor actor,
        string? idempotencyKey,
        CancellationToken cancellationToken);

    /// <summary>One of the account's grants; <c>ops.credit_grant.not_found</c> when it has none with that id.</summary>
    Task<OperationResult<AccountCreditGrantServiceModel>> FindAsync(
        string accountId, Guid grantId, CancellationToken cancellationToken);

    /// <summary>The account's grants oldest first, a page at a time, with the unspent total.</summary>
    Task<OperationResult<AccountCreditGrantsServiceModel>> ListAsync(
        string accountId, string? cursor, int limit, CancellationToken cancellationToken);

    /// <summary>
    /// Takes back what is unspent on one grant, once, with a platform audit row (B-50, AP.5.1). Safe to repeat:
    /// a grant already clawed back answers <see cref="AccountCreditGrantChangeReport.Unchanged"/>.
    /// </summary>
    Task<OperationResult<AccountCreditGrantChangeServiceModel>> ClawBackAsync(
        string accountId,
        Guid grantId,
        ClawBackAccountCreditGrantViewModel model,
        PlatformActor actor,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="IAccountCreditGrantFacade"/>
internal sealed class AccountCreditGrantFacade(
    IAccountCreditGrantBusiness business,
    IIdempotentCommandExecutor idempotency,
    IValidator<GrantAccountCreditsViewModel> grantValidator,
    IValidator<ClawBackAccountCreditGrantViewModel> clawBackValidator) : IAccountCreditGrantFacade
{
    /// <summary>Stable: it is part of every stored idempotency scope for this command.</summary>
    internal const string GrantOperation = "ops.credit_grants.grant";

    public async Task<IdempotentOutcome<AccountCreditGrantChangeServiceModel>> GrantAsync(
        string accountId,
        GrantAccountCreditsViewModel model,
        PlatformActor actor,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(actor);

        if (TooLong(accountId) is { } tooLong)
        {
            return new IdempotentOutcome<AccountCreditGrantChangeServiceModel>(Fail<AccountCreditGrantChangeServiceModel>(tooLong), Replayed: false);
        }

        if (await grantValidator.ValidateAsync(model, cancellationToken) is { IsValid: false } invalid)
        {
            return new IdempotentOutcome<AccountCreditGrantChangeServiceModel>(
                Fail<AccountCreditGrantChangeServiceModel>(Invalid("That grant could not be made.", invalid)),
                Replayed: false);
        }

        var reference = model.ExternalReferenceId?.Trim();
        var reason = model.Reason.Trim();

        // Scoped to the ops client rather than a user, and to this operation: an ops client id is not an Identity
        // account, and the operation name keeps the two from ever sharing a scope. The account is in the
        // fingerprint, so one key cannot grant to two accounts.
        return await idempotency.ExecuteAsync(
            new IdempotentCommand(
                actor.Id,
                WorkspaceId: null,
                GrantOperation,
                idempotencyKey,
                new { accountId, model.Credits, model.Source, reference, reason }),
            async token =>
            {
                var result = await business.GrantAsync(
                    accountId, model.Credits, model.Source, reference, new AccountCreditGrantAudit(actor, reason), token);

                return Report(accountId, result, AccountCreditGrantChangeReport.Granted);
            },
            cancellationToken);
    }

    public async Task<OperationResult<AccountCreditGrantServiceModel>> FindAsync(
        string accountId, Guid grantId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);

        var grant = accountId.Length > BillingPolicy.AccountIdMaxLength
            ? null
            : await business.FindAsync(accountId, grantId, cancellationToken);

        return grant is null
            ? Fail<AccountCreditGrantServiceModel>(NotFound())
            : OperationResult<AccountCreditGrantServiceModel>.Success(grant);
    }

    public async Task<OperationResult<AccountCreditGrantsServiceModel>> ListAsync(
        string accountId, string? cursor, int limit, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);

        if (TooLong(accountId) is { } tooLong)
        {
            return Fail<AccountCreditGrantsServiceModel>(tooLong);
        }

        Guid? after = null;

        if (!string.IsNullOrWhiteSpace(cursor))
        {
            if (!Guid.TryParse(cursor.Trim(), out var id))
            {
                return Fail<AccountCreditGrantsServiceModel>(OperationError.Validation(
                    AccountCreditGrantAdministrationErrors.Unprocessable,
                    "Those grants could not be listed.",
                    [("cursor", "Pass the nextCursor a previous page returned.")]));
            }

            after = id;
        }

        return await business.ListAsync(accountId, after, limit, cancellationToken);
    }

    public async Task<OperationResult<AccountCreditGrantChangeServiceModel>> ClawBackAsync(
        string accountId,
        Guid grantId,
        ClawBackAccountCreditGrantViewModel model,
        PlatformActor actor,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(actor);

        // No account that long can own a grant, so it is the answer an unknown grant gets.
        if (accountId.Length > BillingPolicy.AccountIdMaxLength)
        {
            return Fail<AccountCreditGrantChangeServiceModel>(NotFound());
        }

        if (await clawBackValidator.ValidateAsync(model, cancellationToken) is { IsValid: false } invalid)
        {
            return Fail<AccountCreditGrantChangeServiceModel>(Invalid("That claw-back could not be made.", invalid));
        }

        var result = await business.ClawBackAsync(
            accountId, grantId, new AccountCreditGrantAudit(actor, model.Reason.Trim()), cancellationToken);

        return Report(accountId, result, AccountCreditGrantChangeReport.ClawedBack);
    }

    /// <summary>
    /// A command's outcome as the route reports it. <see cref="AccountCreditGrantOutcome.Unchanged"/> is a success,
    /// not a refusal: what was asked for is already so, and a retry of a command that landed must not read as an
    /// error.
    /// </summary>
    private static OperationResult<AccountCreditGrantChangeServiceModel> Report(
        string accountId, AccountCreditGrantResult result, AccountCreditGrantChangeReport done) => result.Outcome switch
    {
        AccountCreditGrantOutcome.Granted or AccountCreditGrantOutcome.ClawedBack =>
            OperationResult<AccountCreditGrantChangeServiceModel>.Success(
                new AccountCreditGrantChangeServiceModel(accountId, done, result.Grant!)),

        AccountCreditGrantOutcome.Unchanged =>
            OperationResult<AccountCreditGrantChangeServiceModel>.Success(
                new AccountCreditGrantChangeServiceModel(accountId, AccountCreditGrantChangeReport.Unchanged, result.Grant!)),

        AccountCreditGrantOutcome.NotFound => Fail<AccountCreditGrantChangeServiceModel>(NotFound()),

        AccountCreditGrantOutcome.ReferenceUsed => Fail<AccountCreditGrantChangeServiceModel>(new OperationError(
            AccountCreditGrantAdministrationErrors.ReferenceUsed,
            "That reference has already granted credits, to another account or a different number of them.",
            new Dictionary<string, string[]>())),

        _ => Fail<AccountCreditGrantChangeServiceModel>(new OperationError(
            AccountCreditGrantAdministrationErrors.Conflict,
            done is AccountCreditGrantChangeReport.ClawedBack
                ? "This grant's balance changed while it was being clawed back; read it again and decide."
                : "Another grant for that reference was being recorded at the same moment; read the account's grants again and decide.",
            new Dictionary<string, string[]>())),
    };

    /// <summary>
    /// Refused here rather than in Business, which throws on an over-long account id: the id comes off the route,
    /// so an operator's typo would otherwise be a 500 instead of a refusal naming the field.
    /// </summary>
    private static OperationError? TooLong(string accountId) =>
        accountId.Length > BillingPolicy.AccountIdMaxLength
            ? OperationError.Validation(
                AccountCreditGrantAdministrationErrors.Unprocessable,
                "That request could not be made.",
                [("accountId", $"An account id is at most {BillingPolicy.AccountIdMaxLength} characters.")])
            : null;

    private static OperationError NotFound() => new(
        AccountCreditGrantAdministrationErrors.NotFound,
        "This account has no grant with that id.",
        new Dictionary<string, string[]>());

    private static OperationError Invalid(string message, FluentValidation.Results.ValidationResult invalid) =>
        OperationError.Validation(
            AccountCreditGrantAdministrationErrors.Unprocessable,
            message,
            invalid.Errors.Select(error => (error.PropertyName, error.ErrorMessage)));

    private static OperationResult<T> Fail<T>(OperationError error) => OperationResult<T>.Failure(error);
}

using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Billing.Business;
using CreatorPantry.Domain.Modules.Billing.Managers;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Billing.Facade;

/// <summary>
/// The application boundary for which plan an account is on (PRICE-007, AP.4.1). How other modules learn an
/// account's allowance, picture caps and quality classes: through this facade, never the table.
/// </summary>
/// <remarks>
/// <strong>Keyed by account, never by workspace.</strong> A plan is the account's across every workspace it works
/// in (B-47). The caller passes the account it has already established — <c>IWorkspaceContext.AccountId</c> on a
/// creator's request; in a worker, the initiating account recorded on the validated job, <em>not</em> the worker's own
/// context, whose account is the background service identity and would resolve every job to the default plan — and
/// nothing here takes one from a request body, a query or a model.
/// </remarks>
public interface IAccountPlanFacade
{
    /// <summary>The plan the account is on now, with its terms from the catalogue.</summary>
    /// <returns>
    /// Null when no plans are configured — every deployment today — so the caller keeps its flat limits. Otherwise
    /// always a plan: an account with no row, a row that says "the default", or a row naming a plan the catalogue
    /// no longer has all read as <c>Billing:DefaultPlan</c>.
    /// </returns>
    Task<AccountPlanServiceModel?> ResolveAsync(string accountId, CancellationToken cancellationToken);

    /// <summary>The plan the account was on at <paramref name="at"/>, read as <see cref="ResolveAsync(string, CancellationToken)"/> reads now.</summary>
    /// <remarks>
    /// For the allowance period (AP.4.2): a period opens with the plan in force at its own start, so a change made
    /// after the roll waits for the next one even when the account's period row is only written later.
    /// </remarks>
    Task<AccountPlanServiceModel?> ResolveAsync(
        string accountId, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>
    /// Which of <paramref name="qualityClassNames"/> the account's plan allows a picture of, and the cheapest plan
    /// that would allow each of the rest (PRICE-006/007, AP.4.3).
    /// </summary>
    /// <param name="accountId">The account established by the caller, as on <see cref="ResolveAsync(string, CancellationToken)"/>.</param>
    /// <param name="qualityClassNames">
    /// The classes to weigh, in the caller's own spelling — the names come back exactly as they were given, so
    /// nothing outside Billing normalises a class name. A plan spells the same class in the landing page's words
    /// (<c>high-2k</c>) and the comparison ignores case and hyphens.
    /// </param>
    /// <returns>
    /// Null when no plans are configured — every production deployment today; development configures them — so
    /// the caller allows every class it offers,
    /// which is what it did before plans existed. Otherwise one row per name asked about, in that order.
    /// </returns>
    /// <remarks>
    /// Deliberately not "is this class allowed": a picker shows every class it offers with the locked ones named
    /// and priced (AP.3.1A), so the one round trip answers for all of them. Reading one class is the same call
    /// with one name.
    /// </remarks>
    Task<AccountPlanQualityServiceModel?> ResolveQualityAsync(
        string accountId, IReadOnlyCollection<string> qualityClassNames, CancellationToken cancellationToken);

    /// <summary>
    /// Puts an account on a plan, or schedules the change, with a platform audit row per change (PRICE-007,
    /// USAGE-009, AP.4.4).
    /// </summary>
    /// <param name="accountId">From the ops route. An administrator names the account they are changing.</param>
    /// <param name="model">
    /// The key to move to — null for "the configured default" — when it takes effect, and why.
    /// </param>
    /// <param name="actor">
    /// Resolved from the authenticated ops credential, never from the request body. A creator cannot reach this:
    /// it is on an <c>ops</c> route only, and the gateway refuses to proxy those from a browser (B-14).
    /// </param>
    /// <remarks>
    /// <strong>Ops only, and audited, which is why it is on the facade at all.</strong> The write has existed in
    /// Business since AP.4.1 and was deliberately unreachable until it could carry an actor and a reason.
    /// </remarks>
    Task<OperationResult<AccountPlanChangeServiceModel>> ChangeAsync(
        string accountId, SetAccountPlanViewModel model, PlatformActor actor, CancellationToken cancellationToken);

    /// <summary>
    /// Accounts whose plan in force is <paramref name="planKey"/>, by account id, a page at a time (AP.4.4).
    /// </summary>
    /// <remarks>
    /// Identifiers and instants only. An account with no stored row is on the configured default by absence and
    /// is not listed, because Billing has no table of accounts to list from.
    /// </remarks>
    Task<OperationResult<AccountsOnPlanServiceModel>> ListByPlanAsync(
        string planKey, string? cursor, int limit, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IAccountPlanFacade"/>
internal sealed class AccountPlanFacade(
    IAccountPlanBusiness business,
    IValidator<SetAccountPlanViewModel> validator) : IAccountPlanFacade
{
    public Task<AccountPlanServiceModel?> ResolveAsync(string accountId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);

        return business.ResolveAsync(accountId, null, cancellationToken);
    }

    public Task<AccountPlanServiceModel?> ResolveAsync(
        string accountId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);

        return business.ResolveAsync(accountId, at, cancellationToken);
    }

    public Task<AccountPlanQualityServiceModel?> ResolveQualityAsync(
        string accountId, IReadOnlyCollection<string> qualityClassNames, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentNullException.ThrowIfNull(qualityClassNames);

        return business.ResolveQualityAsync(accountId, qualityClassNames, cancellationToken);
    }

    public async Task<OperationResult<AccountPlanChangeServiceModel>> ChangeAsync(
        string accountId, SetAccountPlanViewModel model, PlatformActor actor, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(actor);

        // Refused here rather than in Business, which throws on an over-long account id: the id comes off the
        // route, so an operator's typo would otherwise be a 500 instead of a refusal naming the field.
        if (accountId.Length > BillingPolicy.AccountIdMaxLength)
        {
            return OperationResult<AccountPlanChangeServiceModel>.Failure(OperationError.Validation(
                AccountPlanAdministrationErrors.Unprocessable,
                "That plan change could not be made.",
                [("accountId", $"An account id is at most {BillingPolicy.AccountIdMaxLength} characters.")]));
        }

        if (await validator.ValidateAsync(model, cancellationToken) is { IsValid: false } invalid)
        {
            return OperationResult<AccountPlanChangeServiceModel>.Failure(OperationError.Validation(
                AccountPlanAdministrationErrors.Unprocessable,
                "That plan change could not be made.",
                invalid.Errors.Select(error => (error.PropertyName, error.ErrorMessage))));
        }

        var result = await business.ChangeAsync(
            accountId,
            model.PlanKey?.Trim(),
            AccountPlanSource.Ops,
            model.EffectiveFrom,
            new AccountPlanChangeAudit(actor, model.Reason.Trim()),
            cancellationToken);

        // Unchanged is a success, not a refusal: the account is on the plan that was asked for, which is what
        // the caller wanted, and a retry of a command that already landed must not read as an error.
        if (result.Outcome is not (AccountPlanChangeOutcome.Changed or AccountPlanChangeOutcome.Unchanged))
        {
            return OperationResult<AccountPlanChangeServiceModel>.Failure(Refusal(result.Outcome));
        }

        // Read back rather than described from the request: a change scheduled for later leaves the account on
        // the plan it was already on, and reporting the new one would be a lie the operator acts on. Non-null
        // because a catalogue with no plans cannot take a change at all — ChangeAsync refuses it first.
        var plan = await business.ResolveAsync(accountId, null, cancellationToken);

        var report = result.Outcome is AccountPlanChangeOutcome.Unchanged
            ? AccountPlanChangeReport.Unchanged
            : result.Scheduled ? AccountPlanChangeReport.Scheduled : AccountPlanChangeReport.Changed;

        return OperationResult<AccountPlanChangeServiceModel>.Success(
            new AccountPlanChangeServiceModel(accountId, report, plan!, result.CapsFrom, result.Allowance));
    }

    /// <summary>
    /// Each refused outcome as the problem a client acts on. A contended change is the only one worth retrying,
    /// and even then only after reading the chain again — which is what <c>reread</c> says.
    /// </summary>
    private static OperationError Refusal(AccountPlanChangeOutcome outcome) => outcome switch
    {
        // A host with no catalogue has nothing to put an account on, which is the same answer the read gives
        // rather than a complaint about the key that was sent.
        AccountPlanChangeOutcome.NoCatalogue => new OperationError(
            AccountPlanAdministrationErrors.NoCatalogue,
            "No plan catalogue is configured on this host.",
            new Dictionary<string, string[]>()),

        AccountPlanChangeOutcome.UnknownPlan => OperationError.Validation(
            AccountPlanAdministrationErrors.Unprocessable,
            "That plan change could not be made.",
            [("planKey", "No plan in the catalogue has that key.")]),

        AccountPlanChangeOutcome.InThePast => OperationError.Validation(
            AccountPlanAdministrationErrors.Unprocessable,
            "That plan change could not be made.",
            [("effectiveFrom", "That instant has already passed; a plan's history is not rewritten.")]),

        AccountPlanChangeOutcome.NotAfterLatest => OperationError.Validation(
            AccountPlanAdministrationErrors.Unprocessable,
            "That plan change could not be made.",
            [("effectiveFrom", "That instant is not after the account's latest plan row.")]),

        AccountPlanChangeOutcome.ChangeScheduled => new OperationError(
            AccountPlanAdministrationErrors.ChangeScheduled,
            "A later plan change is already scheduled for this account, so one for now cannot be recorded.",
            new Dictionary<string, string[]>()),

        _ => new OperationError(
            AccountPlanAdministrationErrors.Conflict,
            "This account's plan was changed by someone else; read it again and decide.",
            new Dictionary<string, string[]>()),
    };

    public Task<OperationResult<AccountsOnPlanServiceModel>> ListByPlanAsync(
        string planKey, string? cursor, int limit, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planKey);

        return business.ListByPlanAsync(
            planKey.Trim(),
            string.IsNullOrWhiteSpace(cursor) ? null : cursor.Trim(),
            limit,
            cancellationToken);
    }
}

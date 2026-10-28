using System.Diagnostics;
using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Billing.Data;
using CreatorPantry.Domain.Modules.Billing.Data.Entities;
using CreatorPantry.Domain.Modules.Billing.Managers;

namespace CreatorPantry.Domain.Modules.Billing.Business;

/// <summary>Granting an account credits beyond its allowance, and clawing back what is unspent (PRICE-008, AP.5.1).</summary>
internal interface IAccountCreditGrantBusiness
{
    /// <summary>
    /// Grants <paramref name="credits"/> to the account, once per <paramref name="externalReferenceId"/> within
    /// <paramref name="source"/> when one is given.
    /// </summary>
    /// <param name="audit">
    /// Who granted them and why, for the platform audit row staged with the grant. Null writes no audit row, which
    /// only a caller with an auditable record of its own may do — AP.6.3's verified purchase.
    /// </param>
    Task<AccountCreditGrantResult> GrantAsync(
        string accountId,
        decimal credits,
        CreditGrantSource source,
        string? externalReferenceId,
        AccountCreditGrantAudit? audit,
        CancellationToken cancellationToken);

    /// <summary>Takes back what is unspent on one of the account's grants, once (B-50).</summary>
    Task<AccountCreditGrantResult> ClawBackAsync(
        string accountId, Guid grantId, AccountCreditGrantAudit? audit, CancellationToken cancellationToken);

    /// <summary>One of the account's grants, or null.</summary>
    Task<AccountCreditGrantServiceModel?> FindAsync(string accountId, Guid grantId, CancellationToken cancellationToken);

    /// <summary>The account's grants oldest first, a page at a time, with the unspent total across all of them.</summary>
    Task<OperationResult<AccountCreditGrantsServiceModel>> ListAsync(
        string accountId, Guid? cursor, int limit, CancellationToken cancellationToken);
}

/// <summary>Who granted or clawed back credits and why, for the audit row (AP.5.1).</summary>
internal sealed record AccountCreditGrantAudit(PlatformActor Actor, string Reason);

/// <param name="Grant">The grant as stored after the command, when there is one to report.</param>
internal sealed record AccountCreditGrantResult(AccountCreditGrantOutcome Outcome, AccountCreditGrantServiceModel? Grant = null)
{
    public static AccountCreditGrantResult Refused(AccountCreditGrantOutcome outcome) => new(outcome);
}

/// <inheritdoc cref="IAccountCreditGrantBusiness"/>
internal sealed class AccountCreditGrantBusiness(IAccountCreditGrantDataLayer data, IClock clock) : IAccountCreditGrantBusiness
{
    public async Task<AccountCreditGrantResult> GrantAsync(
        string accountId,
        decimal credits,
        CreditGrantSource source,
        string? externalReferenceId,
        AccountCreditGrantAudit? audit,
        CancellationToken cancellationToken)
    {
        // Refused here rather than left to the schema, because the data layer reads a refused insert as a race and
        // a caller that re-reads and retries on that would retry these forever.
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(accountId.Length, BillingPolicy.AccountIdMaxLength, nameof(accountId));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(credits);
        if (!Enum.IsDefined(source) || source == CreditGrantSource.Unspecified)
        {
            throw new ArgumentOutOfRangeException(nameof(source), source, "A grant names where its credits came from.");
        }

        if (source == CreditGrantSource.Purchase && externalReferenceId is null)
        {
            throw new ArgumentException("A purchase names the checkout that paid for it.", nameof(externalReferenceId));
        }

        // A reference grants once (AP.6.3). Asked before the insert so the common replay — a webhook delivered
        // twice, an operator's double submit with a ticket — writes nothing and says so, rather than failing on the
        // index; the index is still what decides a race between two first deliveries.
        if (externalReferenceId is not null
            && await data.FindByReferenceAsync(source, externalReferenceId, cancellationToken) is { } granted)
        {
            return Replay(granted, accountId, credits);
        }

        var now = clock.UtcNow;
        var grant = new AccountCreditGrant
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Source = source,
            CreditsGranted = credits,
            CreditsRemaining = credits,
            CreditsClawedBack = 0,
            ExternalReferenceId = externalReferenceId,
            GrantedAt = now,
        };

        var entry = audit is null
            ? null
            : Entry(
                audit,
                AccountCreditGrantAuditActions.Granted,
                accountId,
                before: null,
                after: AccountCreditGrantAdministrationPolicy.AuditReference(grant.Id, source, "credits", credits));

        var outcome = await data.AddAsync(grant, entry, cancellationToken);

        if (outcome is AccountCreditGrantOutcome.Granted)
        {
            return new AccountCreditGrantResult(outcome, ToServiceModel(grant));
        }

        // Lost the reference index to a writer that read the same absence: whoever won, the reference has granted
        // now, so the answer is the one a later replay would get.
        if (externalReferenceId is not null
            && await data.FindByReferenceAsync(source, externalReferenceId, cancellationToken) is { } winner)
        {
            return Replay(winner, accountId, credits);
        }

        return AccountCreditGrantResult.Refused(AccountCreditGrantOutcome.Contended);
    }

    public async Task<AccountCreditGrantResult> ClawBackAsync(
        string accountId, Guid grantId, AccountCreditGrantAudit? audit, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);

        var grant = await data.FindForUpdateAsync(accountId, grantId, cancellationToken);

        if (grant is null)
        {
            return AccountCreditGrantResult.Refused(AccountCreditGrantOutcome.NotFound);
        }

        // Once. A clawed-back grant has nothing left to take, and a second claw-back — a refund and then a dispute
        // on the same payment, or an operator's repeat — must not record a second change that did not happen.
        if (grant.ClawedBackAt is not null)
        {
            return new AccountCreditGrantResult(AccountCreditGrantOutcome.Unchanged, ToServiceModel(grant));
        }

        // A grant that is fully spent is still clawed back: nought is taken, because spent credits are not
        // recovered (B-50), and the grant then says it was, so a later spend — or a later look — cannot mistake it
        // for one that stands.
        var taken = grant.CreditsRemaining;
        var entry = audit is null
            ? null
            : Entry(
                audit,
                AccountCreditGrantAuditActions.ClawedBack,
                accountId,
                before: AccountCreditGrantAdministrationPolicy.AuditReference(grant.Id, grant.Source, "remaining", taken),
                after: AccountCreditGrantAdministrationPolicy.AuditReference(grant.Id, grant.Source, "clawedBack", taken));

        grant.CreditsClawedBack = taken;
        grant.CreditsRemaining = 0;
        grant.ClawedBackAt = clock.UtcNow;

        var outcome = await data.SaveClawBackAsync(grant, entry, cancellationToken);

        return outcome is AccountCreditGrantOutcome.ClawedBack
            ? new AccountCreditGrantResult(outcome, ToServiceModel(grant))
            : AccountCreditGrantResult.Refused(outcome);
    }

    public async Task<AccountCreditGrantServiceModel?> FindAsync(
        string accountId, Guid grantId, CancellationToken cancellationToken) =>
        await data.FindAsync(accountId, grantId, cancellationToken) is { } grant ? ToServiceModel(grant) : null;

    public async Task<OperationResult<AccountCreditGrantsServiceModel>> ListAsync(
        string accountId, Guid? cursor, int limit, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);

        AccountCreditGrant? after = null;

        // The cursor is a grant of this account; one of another account's is refused like one that does not exist,
        // so a cursor can never be used to page somebody else's grants.
        if (cursor is { } id)
        {
            after = await data.FindAsync(accountId, id, cancellationToken);

            if (after is null)
            {
                return OperationResult<AccountCreditGrantsServiceModel>.Failure(OperationError.Validation(
                    AccountCreditGrantAdministrationErrors.Unprocessable,
                    "Those grants could not be listed.",
                    [("cursor", "No grant of this account has that id.")]));
            }
        }

        var page = Math.Clamp(limit, 1, AccountCreditGrantAdministrationPolicy.ListMaxLimit);

        // One more than the page, to learn whether there is another without counting the table.
        var rows = await data.ListAsync(accountId, after, page + 1, cancellationToken);
        var remaining = await data.ListRemainingAsync(accountId, cancellationToken);

        var grants = rows.Take(page).Select(ToServiceModel).ToList();

        return OperationResult<AccountCreditGrantsServiceModel>.Success(new AccountCreditGrantsServiceModel(
            accountId,
            remaining.Sum(),
            grants,
            rows.Count > page ? grants[^1].Id.ToString("D") : null));
    }

    /// <summary>
    /// A reference that has already granted: the same grant again when it went to this account for this figure,
    /// and a refusal otherwise — a reference grants once, so a different account or figure is a mistake to show,
    /// not a second grant to make.
    /// </summary>
    private static AccountCreditGrantResult Replay(AccountCreditGrant granted, string accountId, decimal credits) =>
        string.Equals(granted.AccountId, accountId, StringComparison.Ordinal) && granted.CreditsGranted == credits
            ? new AccountCreditGrantResult(AccountCreditGrantOutcome.Unchanged, ToServiceModel(granted))
            : AccountCreditGrantResult.Refused(AccountCreditGrantOutcome.ReferenceUsed);

    private static PlatformAuditEntry Entry(
        AccountCreditGrantAudit audit, string action, string accountId, string? before, string after) =>
        new(
            audit.Actor.Type,
            audit.Actor.Id,
            audit.Actor.Name,
            action,
            AccountCreditGrantAuditActions.SubjectType,
            accountId,
            CorrelationId(),
            audit.Reason,
            before,
            after);

    /// <summary>
    /// The request's trace, so an audit row can be tied to the request that wrote it; a new id when there is no
    /// ambient activity, which is better than a nought nobody can correlate.
    /// </summary>
    private static Guid CorrelationId()
    {
        var traceId = Activity.Current?.TraceId;

        return traceId is { } id && id != default ? Guid.ParseExact(id.ToHexString(), "N") : Guid.NewGuid();
    }

    private static AccountCreditGrantServiceModel ToServiceModel(AccountCreditGrant grant) => new(
        grant.Id,
        grant.AccountId,
        grant.Source,
        grant.CreditsGranted,
        grant.CreditsRemaining,
        grant.CreditsClawedBack,
        grant.ExternalReferenceId,
        grant.GrantedAt,
        grant.ClawedBackAt);
}

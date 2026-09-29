using System.Diagnostics;
using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.AiUsage.Data;
using CreatorPantry.Domain.Modules.AiUsage.Data.Entities;
using CreatorPantry.Domain.Modules.AiUsage.Managers;
using CreatorPantry.Domain.Modules.Auth.Facade;
using Microsoft.Extensions.Options;

namespace CreatorPantry.Domain.Modules.AiUsage.Business;

/// <summary>Platform administration of any account's AI allowance (USAGE-009).</summary>
internal interface IAiUsageAdministrationBusiness
{
    Task<OperationResult<AccountAiUsageAdminServiceModel>> GetAccountAsync(
        string accountId, CancellationToken cancellationToken);

    Task<OperationResult<AiUsageTopConsumersServiceModel>> GetTopConsumersAsync(
        DateTimeOffset from, DateTimeOffset to, int limit, CancellationToken cancellationToken);

    Task<OperationResult<AccountAiQuotaChangeServiceModel>> SetQuotaAsync(
        string accountId, AccountAiQuotaTermsInput terms, string reason, PlatformActor actor,
        CancellationToken cancellationToken);

    Task<OperationResult<AccountAiQuotaChangeServiceModel>> ClearQuotaAsync(
        string accountId, string reason, PlatformActor actor, CancellationToken cancellationToken);

    Task<OperationResult<AccountAiQuotaChangeServiceModel>> SetSuspensionAsync(
        string accountId, bool suspended, string reason, PlatformActor actor,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="IAiUsageAdministrationBusiness"/>
internal sealed class AiUsageAdministrationBusiness(
    IAiUsageAdministrationDataLayer dataLayer,
    IAiQuotaPeriodResolver periods,
    IPlatformAccountFacade accounts,
    ITimeZoneConverter zones,
    IOptions<AiQuotaOptions> options,
    IClock clock) : IAiUsageAdministrationBusiness
{
    private readonly AiQuotaOptions _options = options.Value;

    public async Task<OperationResult<AccountAiUsageAdminServiceModel>> GetAccountAsync(
        string accountId, CancellationToken cancellationToken)
    {
        if (!await accounts.AccountExistsAsync(accountId, cancellationToken))
        {
            return OperationResult<AccountAiUsageAdminServiceModel>.Failure(AccountNotFound());
        }

        var now = clock.UtcNow;

        // Existence, not the row: a read reports whether the terms are the account's own or the platform
        // default, and never writes them. Asking for the entity would attach a mutable one to a GET.
        var hasQuota = await dataLayer.HasCurrentQuotaAsync(accountId, cancellationToken);
        var terms = await periods.ResolveTermsAsync(accountId, cancellationToken);

        // Projected, never opened: an administrator reading an account must not open a period for it. That
        // would give the account a balance row it never asked for and a carry-over computed at a moment
        // nobody chose.
        var period = await periods.ProjectAsync(accountId, terms, now, cancellationToken);

        var byTask = await dataLayer.SumByTaskAsync(
            accountId, period.Id, period.StartsAt, period.EndsAt, cancellationToken);

        var byWorkspace = await dataLayer.SumByWorkspaceAsync(
            accountId, period.Id, period.StartsAt, period.EndsAt, cancellationToken);

        return OperationResult<AccountAiUsageAdminServiceModel>.Success(new AccountAiUsageAdminServiceModel(
            accountId,
            Describe(period, terms.PeriodLength),
            Terms(hasQuota, terms),
            [.. byTask
                .Select(row => new AccountAiUsageTaskTotalServiceModel(row.TaskType, row.Amount, row.Requests))
                .OrderByDescending(row => row.Amount)
                .ThenByDescending(row => row.Requests)
                .ThenBy(row => row.TaskType)],

            // Ids and amounts. No workspace name is resolved here at all — not withheld after being looked
            // up, but never asked for, which is what makes "an administrator sees workspace identifiers"
            // structural rather than a filter somebody could later remove.
            [.. byWorkspace
                .Select(row => new AccountAiUsageWorkspaceTotalAdminServiceModel(
                    row.WorkspaceId, row.Amount, row.Requests))
                .OrderByDescending(row => row.Amount)
                .ThenByDescending(row => row.Requests)
                .ThenBy(row => row.WorkspaceId)]));
    }

    public async Task<OperationResult<AiUsageTopConsumersServiceModel>> GetTopConsumersAsync(
        DateTimeOffset from, DateTimeOffset to, int limit, CancellationToken cancellationToken)
    {
        if (to <= from)
        {
            return Invalid("The window must end after it starts.");
        }

        if (to - from > AiUsageAdministrationPolicy.TopConsumersMaxWindow)
        {
            return Invalid(
                $"The window may span at most {AiUsageAdministrationPolicy.TopConsumersMaxWindow.TotalDays:0} days.");
        }

        var rows = await dataLayer.FindTopConsumersAsync(
            from,
            to,
            Math.Clamp(limit, 1, AiUsageAdministrationPolicy.TopConsumersMaxLimit),
            cancellationToken);

        return OperationResult<AiUsageTopConsumersServiceModel>.Success(new AiUsageTopConsumersServiceModel(
            from,
            to,
            [.. rows.Select(row => new AccountAiConsumerServiceModel(
                row.AccountId, row.Unit, row.Amount, row.Runs))]));

        static OperationResult<AiUsageTopConsumersServiceModel> Invalid(string message) =>
            OperationResult<AiUsageTopConsumersServiceModel>.Failure(new OperationError(
                AiUsageAdministrationErrors.WindowInvalid, message, new Dictionary<string, string[]>()));
    }

    public Task<OperationResult<AccountAiQuotaChangeServiceModel>> SetQuotaAsync(
        string accountId,
        AccountAiQuotaTermsInput input,
        string reason,
        PlatformActor actor,
        CancellationToken cancellationToken)
    {
        if (Reject(input) is { } problem)
        {
            return Task.FromResult(OperationResult<AccountAiQuotaChangeServiceModel>.Failure(problem));
        }

        return ChangeAsync(
            accountId,
            reason,
            actor,
            AiUsageAuditActions.QuotaSet,

            // Suspension is not a term this command touches. An operator raising an allowance is not thereby
            // restoring an account somebody else switched off, and the two have separate routes and separate
            // audit codes precisely so neither can be done by accident.
            current => new AccountAiQuotaWrite(
                input.Unit,
                input.Allowance,
                input.PeriodLength,
                input.PeriodAnchor,
                input.TimeZoneId,
                input.CarryOver,
                input.CarryOverCap,
                current?.IsSuspended ?? false),
            cancellationToken);
    }

    /// <remarks>
    /// <strong>Clearing closes the row rather than nulling its allowance.</strong> Both would fall back to the
    /// configured allowance, but only closing the row returns <em>every</em> term — unit, period length,
    /// anchor, zone, carry-over — to <see cref="AiQuotaOptions"/>, which is what USAGE-003's "an account with
    /// no explicit quota resolves to the configured platform default" says.
    /// </remarks>
    public Task<OperationResult<AccountAiQuotaChangeServiceModel>> ClearQuotaAsync(
        string accountId, string reason, PlatformActor actor, CancellationToken cancellationToken) =>
        ChangeAsync(accountId, reason, actor, AiUsageAuditActions.QuotaCleared, _ => null, cancellationToken);

    /// <remarks>
    /// <para>
    /// <strong>Suspension is stored as a term, and read live.</strong> A period freezes the allowance it
    /// opened with, so a lowered allowance binds at the next roll — but admission reads <c>IsSuspended</c>
    /// from the terms in force now, which is why switching access off takes effect on the very next request.
    /// </para>
    /// <para>
    /// Suspending an account that has no quota row writes one carrying the platform defaults with a
    /// <em>null</em> allowance, so the account keeps resolving to the configured allowance and gains only the
    /// flag. Restoring writes the flag back off rather than removing the row: "restore" undoes a suspension,
    /// and silently discarding terms an operator had also set would be a second, unasked-for change.
    /// </para>
    /// </remarks>
    public Task<OperationResult<AccountAiQuotaChangeServiceModel>> SetSuspensionAsync(
        string accountId,
        bool suspended,
        string reason,
        PlatformActor actor,
        CancellationToken cancellationToken) =>
        ChangeAsync(
            accountId,
            reason,
            actor,
            suspended ? AiUsageAuditActions.AccessSuspended : AiUsageAuditActions.AccessRestored,
            current => new AccountAiQuotaWrite(
                current?.Unit ?? _options.DefaultUnit,
                current?.Allowance,
                current?.PeriodLength ?? _options.DefaultPeriodLength,
                current?.PeriodAnchor ?? _options.DefaultPeriodAnchor,
                current?.TimeZoneId ?? _options.DefaultTimeZoneId,
                current?.CarryOver ?? _options.DefaultCarryOver,
                current?.CarryOverCap ?? _options.DefaultCarryOverCap,
                suspended),
            cancellationToken);

    /// <summary>
    /// The one write path: resolve the account, read the open row, decide the replacement, and either do
    /// nothing or close-insert-audit in a single transaction.
    /// </summary>
    /// <remarks>
    /// <strong>A command that changes nothing writes nothing — including no audit row.</strong> That is what
    /// makes a retried administrative command safe without an idempotency key: the second attempt observes
    /// the terms it wanted already in force and reports <c>Unchanged</c>. An audit trail that recorded every
    /// retry as a change would also be a trail that could not be counted.
    /// </remarks>
    private async Task<OperationResult<AccountAiQuotaChangeServiceModel>> ChangeAsync(
        string accountId,
        string reason,
        PlatformActor actor,
        string action,
        Func<AccountAiQuota?, AccountAiQuotaWrite?> replacement,
        CancellationToken cancellationToken)
    {
        if (!await accounts.AccountExistsAsync(accountId, cancellationToken))
        {
            return OperationResult<AccountAiQuotaChangeServiceModel>.Failure(AccountNotFound());
        }

        var current = await dataLayer.FindCurrentQuotaAsync(accountId, cancellationToken);
        var before = Terms(current is not null, await periods.ResolveTermsAsync(accountId, cancellationToken));
        var target = replacement(current);

        if (NoChange(current, target))
        {
            return OperationResult<AccountAiQuotaChangeServiceModel>.Success(
                new AccountAiQuotaChangeServiceModel(accountId, AccountAiQuotaChangeOutcome.Unchanged, before));
        }

        var after = Terms(target);

        var outcome = await dataLayer.ReplaceQuotaAsync(
            accountId,
            current,
            target,
            new PlatformAuditEntry(
                actor.Type,
                actor.Id,
                actor.Name,
                action,
                AiUsageAuditActions.SubjectType,
                accountId,
                CorrelationId(),
                reason,
                AccountAiQuotaAuditReference.For(before),
                AccountAiQuotaAuditReference.For(after)),
            cancellationToken);

        if (outcome is AiQuotaAdministrationWriteOutcome.Contended)
        {
            return OperationResult<AccountAiQuotaChangeServiceModel>.Failure(new OperationError(
                AiUsageAdministrationErrors.QuotaConflict,
                "Another change to this account's quota committed first. Re-read the account and decide again.",
                new Dictionary<string, string[]>()));
        }

        return OperationResult<AccountAiQuotaChangeServiceModel>.Success(
            new AccountAiQuotaChangeServiceModel(accountId, AccountAiQuotaChangeOutcome.Changed, after));
    }

    /// <summary>Whether the requested terms are already the ones in force, field for field.</summary>
    private static bool NoChange(AccountAiQuota? current, AccountAiQuotaWrite? target) =>
        (current, target) switch
        {
            (null, null) => true,
            (null, not null) or (not null, null) => false,
            var (row, wanted) => row.Unit == wanted.Unit
                && row.Allowance == wanted.Allowance
                && row.PeriodLength == wanted.PeriodLength
                && row.PeriodAnchor == wanted.PeriodAnchor
                && string.Equals(row.TimeZoneId, wanted.TimeZoneId, StringComparison.Ordinal)
                && row.CarryOver == wanted.CarryOver
                && row.CarryOverCap == wanted.CarryOverCap
                && row.IsSuspended == wanted.IsSuspended,
        };

    /// <summary>
    /// The combinations the quota table's own check constraints forbid, refused here with a field error
    /// rather than reaching the database and coming back as a 500.
    /// </summary>
    private OperationError? Reject(AccountAiQuotaTermsInput input)
    {
        List<(string Field, string Error)> errors = [];

        if (input.Unit is AiQuotaUnit.Unspecified)
        {
            errors.Add((nameof(input.Unit), "Choose a unit."));
        }

        if (input.PeriodLength is AiQuotaPeriodLength.Unspecified)
        {
            errors.Add((nameof(input.PeriodLength), "Choose a period length."));
        }

        if (input.CarryOver is AiQuotaCarryOver.Unspecified)
        {
            errors.Add((nameof(input.CarryOver), "Choose a carry-over rule."));
        }

        if (input.Allowance is < 0m)
        {
            errors.Add((nameof(input.Allowance), "An allowance cannot be negative."));
        }

        var anchorError = input.PeriodLength switch
        {
            AiQuotaPeriodLength.Daily when input.PeriodAnchor != 0 =>
                "A daily period has no anchor; use 0.",
            AiQuotaPeriodLength.Weekly when input.PeriodAnchor is < 0 or > 6 =>
                "A weekly anchor is a day of the week, 0 (Sunday) to 6.",
            AiQuotaPeriodLength.Monthly when input.PeriodAnchor is < 1
                || input.PeriodAnchor > AiUsagePolicy.MonthlyAnchorMax =>
                $"A monthly anchor is a day of the month, 1 to {AiUsagePolicy.MonthlyAnchorMax}.",
            _ => null,
        };

        if (anchorError is not null)
        {
            errors.Add((nameof(input.PeriodAnchor), anchorError));
        }

        switch (input.CarryOver)
        {
            case AiQuotaCarryOver.Unused when input.CarryOverCap is null or < 0m:
                errors.Add((nameof(input.CarryOverCap),
                    "Carrying unused allowance forward needs a cap of zero or more; without one the balance is unbounded."));
                break;
            case AiQuotaCarryOver.None when input.CarryOverCap is not null:
                errors.Add((nameof(input.CarryOverCap), "A cap only applies when unused allowance carries forward."));
                break;
            default:
                break;
        }

        if (string.IsNullOrWhiteSpace(input.TimeZoneId) || !zones.IsValidZone(input.TimeZoneId))
        {
            errors.Add((nameof(input.TimeZoneId), "Supply an IANA time zone identifier, such as Europe/London."));
        }

        return errors.Count == 0
            ? null
            : OperationError.Validation(
                AiUsageAdministrationErrors.QuotaInvalid, "The requested quota terms are not valid.", errors);
    }

    private static OperationError AccountNotFound() => new(
        AiUsageAdministrationErrors.AccountNotFound,
        "No such account.",
        new Dictionary<string, string[]>());

    /// <summary>
    /// The terms in force, described. <paramref name="hasQuota"/> false means the platform default.
    /// </summary>
    private static AccountAiQuotaTermsServiceModel Terms(bool hasQuota, AiQuotaTerms resolved) =>
        new(hasQuota ? AccountAiQuotaSource.Account : AccountAiQuotaSource.PlatformDefault,
            resolved.Unit,
            resolved.Allowance,
            resolved.PeriodLength,
            resolved.PeriodAnchor,
            resolved.TimeZoneId,
            resolved.CarryOver,
            resolved.CarryOverCap,
            resolved.IsSuspended);

    /// <summary>
    /// The terms a pending write will put in force, described without re-reading the database.
    /// </summary>
    /// <remarks>
    /// A null <paramref name="write"/> is a clear, which leaves the account on the configured default — so the
    /// allowance reported is the option value, exactly as <c>AiQuotaPeriodResolver.ResolveTermsAsync</c> would
    /// resolve it a moment later.
    /// </remarks>
    private AccountAiQuotaTermsServiceModel Terms(AccountAiQuotaWrite? write) =>
        write is null
            ? new AccountAiQuotaTermsServiceModel(
                AccountAiQuotaSource.PlatformDefault,
                _options.DefaultUnit,
                _options.DefaultAllowance,
                _options.DefaultPeriodLength,
                _options.DefaultPeriodAnchor,
                _options.DefaultTimeZoneId,
                _options.DefaultCarryOver,
                _options.DefaultCarryOverCap,
                IsSuspended: false)
            : new AccountAiQuotaTermsServiceModel(
                AccountAiQuotaSource.Account,
                write.Unit,
                write.Allowance ?? _options.DefaultAllowance,
                write.PeriodLength,
                write.PeriodAnchor,
                write.TimeZoneId,
                write.CarryOver,
                write.CarryOverCap,
                write.IsSuspended);

    private static AccountAiUsagePeriodServiceModel Describe(
        AccountAiQuotaPeriod period, AiQuotaPeriodLength length) =>
        new(period.Unit,
            period.Allowance + period.CarriedOver,
            period.CarriedOver,
            period.Consumed,
            period.Reserved,
            AiQuotaPeriodCalculator.Remaining(
                period.Allowance, period.CarriedOver, period.Consumed, period.Reserved),
            length,
            period.TimeZoneId,
            period.StartsAt,
            period.EndsAt);

    /// <summary>
    /// The ambient trace id, so an audit row can be tied back to the request that wrote it.
    /// </summary>
    /// <remarks>
    /// The same derivation <c>IRecipeBusiness.CorrelationId()</c> uses, and deliberately identical: a W3C
    /// trace id is sixteen bytes, the same width as a Guid, so the two are one value in two spellings and an
    /// operator can paste an audit row's correlation id into a trace search. A new id when nothing is traced,
    /// so an untraced entry is still correlatable with itself rather than sharing <c>Guid.Empty</c> with
    /// every other one.
    /// </remarks>
    private static Guid CorrelationId()
    {
        var traceId = Activity.Current?.TraceId;

        return traceId is { } id && id != default ? Guid.ParseExact(id.ToHexString(), "N") : Guid.NewGuid();
    }
}

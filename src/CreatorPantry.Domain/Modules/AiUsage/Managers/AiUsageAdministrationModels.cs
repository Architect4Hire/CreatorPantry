using CreatorPantry.Domain.Managers.Audit;

namespace CreatorPantry.Domain.Modules.AiUsage.Managers;

/// <summary>Where the terms an account is spending under came from.</summary>
public enum AccountAiQuotaSource
{
    /// <summary>No quota row of its own: every term comes from <see cref="AiQuotaOptions"/> (USAGE-003).</summary>
    PlatformDefault = 1,

    /// <summary>An open <c>AccountAiQuota</c> row. A null allowance there still falls back to the default.</summary>
    Account = 2,
}

/// <summary>
/// The terms in force for an account right now, and where they came from.
/// </summary>
/// <remarks>
/// <strong>Reported beside the running period, not instead of it.</strong> A period freezes its allowance when
/// it opens, so lowering an allowance today does not shrink the period already running — an operator reading
/// only the period would conclude their change had not landed. Suspension is the exception and is read live,
/// which is why it appears here rather than on the period.
/// </remarks>
public sealed record AccountAiQuotaTermsServiceModel(
    AccountAiQuotaSource Source,
    AiQuotaUnit Unit,
    decimal Allowance,
    AiQuotaPeriodLength PeriodLength,
    int PeriodAnchor,
    string TimeZoneId,
    AiQuotaCarryOver CarryOver,
    decimal? CarryOverCap,
    bool IsSuspended);

/// <summary>
/// What work in one workspace charged an account this period, for a platform administrator.
/// </summary>
/// <param name="WorkspaceId">
/// The identifier only. <strong>Deliberately no name</strong>: USAGE-009 lets an administrator see numbers,
/// accounts and workspace identifiers, and a workspace's name is a fact about a workspace they hold no
/// membership in. The creator's own view names the workspaces they still belong to; this one never does.
/// </param>
public sealed record AccountAiUsageWorkspaceTotalAdminServiceModel(
    Guid? WorkspaceId, decimal Amount, int Requests);

/// <summary>Any account's allowance and consumption, read by a platform administrator (USAGE-009).</summary>
/// <param name="AccountId">Echoed back, so a response cannot be mistaken for another account's.</param>
public sealed record AccountAiUsageAdminServiceModel(
    string AccountId,
    AccountAiUsagePeriodServiceModel Period,
    AccountAiQuotaTermsServiceModel Terms,
    IReadOnlyList<AccountAiUsageTaskTotalServiceModel> ByTask,
    IReadOnlyList<AccountAiUsageWorkspaceTotalAdminServiceModel> ByWorkspace);

/// <summary>One account's consumption over the requested window.</summary>
/// <param name="Runs">
/// How many charged runs produced <paramref name="Amount"/>. Runs rather than provider attempts, because both
/// numbers then come from the same rows and cannot disagree: a leaderboard whose count and total were read
/// from two tables would invite exactly the question it exists to answer.
/// </param>
public sealed record AccountAiConsumerServiceModel(
    string AccountId, AiQuotaUnit Unit, decimal Amount, int Runs);

/// <summary>The highest-consuming accounts for a window (USAGE-009).</summary>
/// <param name="From">Inclusive.</param>
/// <param name="To">Exclusive.</param>
public sealed record AiUsageTopConsumersServiceModel(
    DateTimeOffset From,
    DateTimeOffset To,
    IReadOnlyList<AccountAiConsumerServiceModel> Accounts);

/// <summary>What a quota command did, so a caller can tell a change from a repeat.</summary>
public enum AccountAiQuotaChangeOutcome
{
    /// <summary>The terms already said this. Nothing was written and no audit event was raised.</summary>
    Unchanged = 1,

    /// <summary>The open row was closed and a new one opened.</summary>
    Changed = 2,
}

/// <summary>The result of setting, clearing, suspending or restoring (USAGE-009).</summary>
/// <param name="Terms">The terms in force after the command, so a caller need not re-read to confirm.</param>
public sealed record AccountAiQuotaChangeServiceModel(
    string AccountId,
    AccountAiQuotaChangeOutcome Outcome,
    AccountAiQuotaTermsServiceModel Terms);

/// <summary>The terms an administrator is asking for, already shape-validated at the edge.</summary>
public sealed record AccountAiQuotaTermsInput(
    AiQuotaUnit Unit,
    decimal? Allowance,
    AiQuotaPeriodLength PeriodLength,
    int PeriodAnchor,
    string TimeZoneId,
    AiQuotaCarryOver CarryOver,
    decimal? CarryOverCap);

/// <summary>Stable error codes for the ops quota routes. The suffix selects the HTTP status.</summary>
public static class AiUsageAdministrationErrors
{
    /// <summary>No such Identity account.</summary>
    public const string AccountNotFound = "ops.account.not_found";

    /// <summary>Another writer changed the account's terms first; the caller re-reads and decides again.</summary>
    public const string QuotaConflict = "ops.account_quota.conflict";

    /// <summary>The requested terms are not a combination the quota table permits.</summary>
    public const string QuotaInvalid = "ops.account_quota.invalid";

    /// <summary>The requested window is empty, backwards, or longer than the platform will scan.</summary>
    public const string WindowInvalid = "ops.usage_window.invalid";
}

/// <summary>Audit action codes and the subject type they are written against. Append-only.</summary>
public static class AiUsageAuditActions
{
    public const string SubjectType = "Account";

    public const string QuotaSet = "account.ai_quota.set";
    public const string QuotaCleared = "account.ai_quota.cleared";
    public const string AccessSuspended = "account.ai_access.suspended";
    public const string AccessRestored = "account.ai_access.restored";
}

/// <summary>Bounds for the ops usage routes, published so a client need not discover them by being refused.</summary>
public static class AiUsageAdministrationPolicy
{
    public const int TopConsumersDefaultLimit = 20;
    public const int TopConsumersMaxLimit = 100;

    /// <summary>The longest window the top-consumers query will scan.</summary>
    public static readonly TimeSpan TopConsumersMaxWindow = TimeSpan.FromDays(400);

    /// <summary>A reason is required on every change and is stored in the platform audit row.</summary>
    public const int ReasonMinLength = 3;

    /// <inheritdoc cref="ReasonMinLength"/>
    public const int ReasonMaxLength = AuditPolicy.SummaryMaxLength;
}

/// <summary>Formats a set of terms as the compact audit pointer <c>PlatformAuditLog</c> asks for.</summary>
/// <remarks>
/// Field names and values, bounded and safe to display — never prose, and never anything from a workspace.
/// No capability, recipe, prompt or proposal is named here: a quota change is about numbers.
/// </remarks>
public static class AccountAiQuotaAuditReference
{
    /// <summary>The pointer written when an account has no quota row of its own.</summary>
    public const string PlatformDefault = "default";

    public static string For(AccountAiQuotaTermsServiceModel terms) =>
        terms.Source is AccountAiQuotaSource.PlatformDefault
            ? PlatformDefault
            : Truncate(
                $"allowance={terms.Allowance};unit={terms.Unit};period={terms.PeriodLength};"
                + $"anchor={terms.PeriodAnchor};zone={terms.TimeZoneId};carry={terms.CarryOver};"
                + $"cap={terms.CarryOverCap?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"};"
                + $"suspended={(terms.IsSuspended ? "true" : "false")}");

    private static string Truncate(string value) =>
        value.Length <= AuditPolicy.ReferenceMaxLength ? value : value[..AuditPolicy.ReferenceMaxLength];
}

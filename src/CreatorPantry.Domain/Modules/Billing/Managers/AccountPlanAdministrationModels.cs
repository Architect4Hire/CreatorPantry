using CreatorPantry.Domain.Managers.Audit;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Billing.Managers;

/// <summary>
/// What a platform administrator sends to put an account on a plan (PRICE-007, USAGE-009, AP.4.4).
/// </summary>
/// <param name="PlanKey">
/// The catalogue key to put the account on, or null for "the configured default" — which is what a stored row
/// with no key means, so the two say the same thing and an account tracks a change of
/// <c>Billing:DefaultPlan</c> afterwards.
/// </param>
/// <param name="EffectiveFrom">
/// When the change takes effect, in UTC; null for now. A future instant schedules it, which is how a downgrade
/// at period end is recorded (B-54).
/// </param>
/// <param name="Reason">
/// Why, in the operator's own words. Required, as it is on every other ops change: the audit row demands one.
/// </param>
public sealed record SetAccountPlanViewModel(string? PlanKey, DateTimeOffset? EffectiveFrom, string Reason);

/// <summary>Shape rules for <see cref="SetAccountPlanViewModel"/>; whether the plan exists is Business's.</summary>
public sealed class SetAccountPlanViewModelValidator : AbstractValidator<SetAccountPlanViewModel>
{
    public SetAccountPlanViewModelValidator()
    {
        // Null is a value here — "the default" — so only a key that is present is measured.
        RuleFor(model => model.PlanKey)
            .MaximumLength(BillingPolicy.PlanKeyMaxLength)
            .When(model => model.PlanKey is not null);

        RuleFor(model => model.PlanKey)
            .Must(key => !string.IsNullOrWhiteSpace(key))
            .When(model => model.PlanKey is not null)
            .WithMessage("Leave planKey out to say 'the configured default'; do not send a blank one.");

        RuleFor(model => model.Reason)
            .NotEmpty()
            .MinimumLength(AccountPlanAdministrationPolicy.ReasonMinLength)
            .MaximumLength(AccountPlanAdministrationPolicy.ReasonMaxLength);
    }
}

/// <summary>What a plan change did, as the ops route reports it (AP.4.4).</summary>
/// <param name="AccountId">The account the change was made against.</param>
/// <param name="Outcome">
/// Whether the change took effect now, was recorded for later, or changed nothing because the account was
/// already on that plan from that instant.
/// </param>
/// <param name="Plan">The plan in force now, read after the write: the new one, or the old one on a schedule.</param>
/// <param name="CapsFrom">
/// When the new plan's daily and running picture caps and its quality classes apply: now for a change in force, the
/// start for a scheduled one, null when nothing changed (DL.3). They are read at each request, so nothing waits.
/// </param>
/// <param name="Allowance">When the new plan's allowance applies (B-56, DL.3).</param>
public sealed record AccountPlanChangeServiceModel(
    string AccountId,
    AccountPlanChangeReport Outcome,
    AccountPlanServiceModel Plan,
    DateTimeOffset? CapsFrom,
    AccountPlanAllowanceTiming Allowance);

/// <summary>When a plan change's allowance applies, by B-56's rule (DL.3).</summary>
public enum AccountPlanAllowanceTiming
{
    /// <summary>Nothing changed.</summary>
    None = 0,

    /// <summary>
    /// The new plan grants more credits: the difference is added from the change's start, at the account's next AI
    /// request, on top of what the running period already granted.
    /// </summary>
    DifferenceNow = 1,

    /// <summary>
    /// The new plan grants the same or fewer credits, or is granted once (the trial): the running period keeps what it
    /// opened with, and the new allowance arrives at the first period that opens after the change starts (B-54).
    /// </summary>
    NextPeriod = 2,
}

/// <summary>What the ops route reports a plan change as (AP.4.4).</summary>
public enum AccountPlanChangeReport
{
    /// <summary>In force from now.</summary>
    Changed = 1,

    /// <summary>Recorded for a later instant, such as a downgrade at period end (B-54).</summary>
    Scheduled = 2,

    /// <summary>
    /// Nothing was written, because the account was already on that plan from that instant.
    /// </summary>
    /// <remarks>
    /// What makes this <c>PUT</c> safe to retry without an idempotency key, and the answer its sibling
    /// <c>SetQuotaAsync</c> gives for the same case: a plan chain is append-only, so a repeated command would
    /// otherwise add a second row and a second audit entry recording a change that did not happen.
    /// </remarks>
    Unchanged = 3,
}

/// <summary>
/// One account listed under a plan (AP.4.4). Identifiers and instants only — an administrator counting who is on
/// what is not a member of anybody's workspace.
/// </summary>
/// <param name="PlanKey">
/// The key its row in force stores, or null when that row says "the configured default".
/// </param>
public sealed record AccountOnPlanServiceModel(
    string AccountId, string? PlanKey, AccountPlanSource Source, DateTimeOffset EffectiveFrom);

/// <summary>A page of accounts on one plan, with the cursor that continues it.</summary>
/// <param name="NextCursor">
/// The account id to pass as <c>cursor</c> for the next page, or null at the end. The ordering is by account id,
/// which never changes, so a cursor stays valid while plans move underneath it.
/// </param>
public sealed record AccountsOnPlanServiceModel(
    string PlanKey, IReadOnlyList<AccountOnPlanServiceModel> Accounts, string? NextCursor);

/// <summary>Problem codes the plan administration routes answer with. Append-only.</summary>
public static class AccountPlanAdministrationErrors
{
    /// <summary>
    /// The key names no plan in the catalogue, the start has already passed, or it is not after the account's
    /// latest row. Maps to 422: well formed, and still not a change this catalogue or this chain can take.
    /// </summary>
    public const string Unprocessable = "ops.account_plan.unprocessable";

    /// <summary>
    /// Another writer changed the account's plan first. Maps to 409 and is classified <c>reread</c>: the chain
    /// moved, so the operator reads it again and decides against what is there now.
    /// </summary>
    public const string Conflict = "ops.account_plan.conflict";

    /// <summary>
    /// A change for now was asked while a later one is already scheduled. Maps to 409 and is a reviewed state
    /// conflict rather than a retry: replacing a scheduled change is AP.6.2's to design, so nothing is written.
    /// </summary>
    public const string ChangeScheduled = "ops.account_plan.change_scheduled.conflict";

    /// <summary>
    /// This host configures no plan catalogue, so there is no plan to report, list, or move an account to. Maps
    /// to 404: the thing being asked about does not exist here, and an invented default would be a plan nobody
    /// is on.
    /// </summary>
    /// <remarks>
    /// Named for the catalogue and not the account, because a client matching <c>account_plan.not_found</c>
    /// would otherwise read it as "no such account" — which this is not, and which these routes never say: an
    /// account identifier is not verified against Identity here at all.
    /// </remarks>
    public const string NoCatalogue = "ops.account_plan.catalogue.not_found";
}

/// <summary>Audit action codes and the subject they are written against. Append-only.</summary>
public static class AccountPlanAuditActions
{
    public const string SubjectType = "Account";

    /// <summary>A change in force from now.</summary>
    public const string Set = "account.plan.set";

    /// <summary>A change recorded for a later instant, such as a downgrade at period end (B-54).</summary>
    public const string Scheduled = "account.plan.scheduled";
}

/// <summary>Bounds for the plan administration routes, published so a client need not discover them by refusal.</summary>
public static class AccountPlanAdministrationPolicy
{
    public const int ListDefaultLimit = 20;
    public const int ListMaxLimit = 100;

    /// <summary>A reason is required on every change and is stored in the platform audit row.</summary>
    public const int ReasonMinLength = 3;

    /// <inheritdoc cref="ReasonMinLength"/>
    public const int ReasonMaxLength = AuditPolicy.SummaryMaxLength;

    /// <summary>
    /// A plan as the compact audit pointer <c>PlatformAuditLog</c> asks for: the key and, for a scheduled
    /// change, when it starts. Field names and bounded values, never prose and never anything from a workspace.
    /// </summary>
    /// <param name="planKey">The key, or null for "the configured default", which is written as such.</param>
    public static string AuditReference(string? planKey, DateTimeOffset? effectiveFrom = null)
    {
        var key = $"planKey={planKey ?? "(default)"}";

        return effectiveFrom is { } from
            ? $"{key};effectiveFrom={from.ToUniversalTime():O}"
            : key;
    }
}

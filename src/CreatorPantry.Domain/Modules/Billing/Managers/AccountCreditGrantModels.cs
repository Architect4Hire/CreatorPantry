using System.Globalization;
using CreatorPantry.Domain.Managers.Audit;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Billing.Managers;

/// <summary>What a platform administrator sends to grant an account credits by hand (PRICE-008, AP.5.1).</summary>
/// <param name="Credits">
/// How many, above nought and at most four decimal places — the precision a period's allowance is kept to.
/// </param>
/// <param name="Source">
/// <see cref="CreditGrantSource.Ops"/> or <see cref="CreditGrantSource.Promo"/>. A
/// <see cref="CreditGrantSource.Purchase"/> is granted by the payment that made it (AP.6.3), never by hand.
/// </param>
/// <param name="ExternalReferenceId">
/// Optional: the ticket or campaign the grant answers to. Unique within its source, so a grant entered twice
/// against one ticket is entered once.
/// </param>
/// <param name="Reason">Why, in the operator's own words. Required, and stored in the platform audit row.</param>
public sealed record GrantAccountCreditsViewModel(
    decimal Credits, CreditGrantSource Source, string? ExternalReferenceId, string Reason);

/// <summary>Shape rules for <see cref="GrantAccountCreditsViewModel"/>; whether a reference is free is Business's.</summary>
public sealed class GrantAccountCreditsViewModelValidator : AbstractValidator<GrantAccountCreditsViewModel>
{
    public GrantAccountCreditsViewModelValidator()
    {
        RuleFor(model => model.Credits)
            .GreaterThan(0)
            .PrecisionScale(BillingPolicy.CreditPrecision, BillingPolicy.CreditScale, ignoreTrailingZeros: true);

        RuleFor(model => model.Source)
            .Must(source => source is CreditGrantSource.Ops or CreditGrantSource.Promo)
            .WithMessage(model => model.Source is CreditGrantSource.Purchase
                ? "A purchase is granted by the payment that made it, never by hand."
                : "Say whether this grant is Ops or Promo.");

        RuleFor(model => model.ExternalReferenceId)
            .MaximumLength(BillingPolicy.ExternalReferenceMaxLength)
            .Must(reference => !string.IsNullOrWhiteSpace(reference))
            .When(model => model.ExternalReferenceId is not null)
            .WithMessage("Leave externalReferenceId out when there is none; do not send a blank one.");

        RuleFor(model => model.Reason)
            .NotEmpty()
            .MinimumLength(AccountCreditGrantAdministrationPolicy.ReasonMinLength)
            .MaximumLength(AccountCreditGrantAdministrationPolicy.ReasonMaxLength);
    }
}

/// <summary>What a platform administrator sends to claw back the unspent part of a grant (B-50, AP.5.1).</summary>
/// <param name="Reason">Why. Required, and stored in the platform audit row.</param>
public sealed record ClawBackAccountCreditGrantViewModel(string Reason);

/// <summary>Shape rules for <see cref="ClawBackAccountCreditGrantViewModel"/>.</summary>
public sealed class ClawBackAccountCreditGrantViewModelValidator : AbstractValidator<ClawBackAccountCreditGrantViewModel>
{
    public ClawBackAccountCreditGrantViewModelValidator()
    {
        RuleFor(model => model.Reason)
            .NotEmpty()
            .MinimumLength(AccountCreditGrantAdministrationPolicy.ReasonMinLength)
            .MaximumLength(AccountCreditGrantAdministrationPolicy.ReasonMaxLength);
    }
}

/// <summary>One grant of credits to an account, and what is left of it (PRICE-008, AP.5.1).</summary>
/// <param name="CreditsClawedBack">
/// The unspent credits a claw-back took; nought while the grant stands. What was spent is the grant less this
/// and <paramref name="CreditsRemaining"/>, and is not stated separately so that it cannot disagree with them.
/// </param>
/// <param name="ClawedBackAt">When the grant was clawed back, or null while it stands. No grant expires (B-50).</param>
public sealed record AccountCreditGrantServiceModel(
    Guid Id,
    string AccountId,
    CreditGrantSource Source,
    decimal CreditsGranted,
    decimal CreditsRemaining,
    decimal CreditsClawedBack,
    string? ExternalReferenceId,
    DateTimeOffset GrantedAt,
    DateTimeOffset? ClawedBackAt);

/// <summary>What a grant or a claw-back did, as the ops route reports it (AP.5.1).</summary>
/// <param name="Grant">The grant as stored after the command: the new one, or the one that was already there.</param>
public sealed record AccountCreditGrantChangeServiceModel(
    string AccountId, AccountCreditGrantChangeReport Outcome, AccountCreditGrantServiceModel Grant);

/// <summary>What the ops routes report a grant command as (AP.5.1).</summary>
public enum AccountCreditGrantChangeReport
{
    /// <summary>A new grant was recorded.</summary>
    Granted = 1,

    /// <summary>The grant's unspent credits were taken back.</summary>
    ClawedBack = 2,

    /// <summary>
    /// Nothing was written: the reference had already granted exactly this to this account, or the grant had
    /// already been clawed back.
    /// </summary>
    /// <remarks>
    /// What lets a claw-back be sent again, and a referenced grant arrive twice, without writing a second change or
    /// a second audit entry — the answer <c>AccountPlanChangeReport.Unchanged</c> gives a repeated plan change.
    /// </remarks>
    Unchanged = 3,
}

/// <summary>What a grant command did, between Business and the data layer (AP.5.1).</summary>
internal enum AccountCreditGrantOutcome
{
    Granted = 1,

    ClawedBack = 2,

    /// <summary>
    /// Nothing was written: the reference had already granted exactly this to this account, or the grant had
    /// already been clawed back.
    /// </summary>
    Unchanged = 3,

    /// <summary>The account has no grant with that id.</summary>
    NotFound = 4,

    /// <summary>The grant's balance moved after it was read; the caller re-reads and decides again.</summary>
    Contended = 5,

    /// <summary>The reference already granted credits to another account, or a different number of them.</summary>
    ReferenceUsed = 6,
}

/// <summary>
/// One account's grants, oldest first, a page at a time, with the unspent credits across all of them (AP.5.1).
/// </summary>
/// <param name="CreditsRemaining">
/// The unspent credits across every grant the account has, not just this page's — what an operator is asking
/// when they look an account up.
/// </param>
/// <param name="NextCursor">
/// The grant id to pass as <c>cursor</c> for the next page, or null at the end. Grants are listed in the order
/// they were made, which never changes, so a cursor stays valid while balances move underneath it.
/// </param>
public sealed record AccountCreditGrantsServiceModel(
    string AccountId,
    decimal CreditsRemaining,
    IReadOnlyList<AccountCreditGrantServiceModel> Grants,
    string? NextCursor);

/// <summary>Problem codes the credit grant routes answer with. Append-only.</summary>
public static class AccountCreditGrantAdministrationErrors
{
    /// <summary>
    /// The grant or claw-back is not well formed — no reason, a purchase by hand, credits of nought — or the
    /// cursor names no grant of this account. Maps to 422.
    /// </summary>
    public const string Unprocessable = "ops.credit_grant.unprocessable";

    /// <summary>
    /// No grant has that id on that account. Maps to 404; a grant of another account answers the same, so an
    /// account in the route can never be used to act on somebody else's credits.
    /// </summary>
    public const string NotFound = "ops.credit_grant.not_found";

    /// <summary>
    /// The grant's balance moved between reading it and clawing it back — a spend, or another claw-back — or, far
    /// more rarely, a grant lost a race for its reference and the winner could not be read. Maps to 409 and is
    /// classified <c>reread</c>: read again and decide against what is there now.
    /// </summary>
    public const string Conflict = "ops.credit_grant.conflict";

    /// <summary>
    /// The external reference has already granted credits — to another account, or a different figure. Maps to 409
    /// and is a reviewed state conflict: sending it again cannot succeed, because a reference grants once.
    /// </summary>
    public const string ReferenceUsed = "ops.credit_grant.reference_used.conflict";
}

/// <summary>Audit action codes for credit grants. Append-only.</summary>
public static class AccountCreditGrantAuditActions
{
    /// <summary>The account the credits belong to, as the plan's audit rows name it.</summary>
    public const string SubjectType = AccountPlanAuditActions.SubjectType;

    public const string Granted = "account.credits.granted";

    public const string ClawedBack = "account.credits.clawed_back";
}

/// <summary>Bounds for the credit grant routes, published so a client need not discover them by refusal.</summary>
public static class AccountCreditGrantAdministrationPolicy
{
    public const int ListDefaultLimit = 20;
    public const int ListMaxLimit = 100;

    /// <summary>A reason is required on every grant and claw-back and is stored in the platform audit row.</summary>
    public const int ReasonMinLength = 3;

    /// <inheritdoc cref="ReasonMinLength"/>
    public const int ReasonMaxLength = AuditPolicy.SummaryMaxLength;

    /// <summary>
    /// A grant's figures as the compact audit pointer <c>PlatformAuditLog</c> asks for: which grant, from what
    /// source, and how many credits. Field names and bounded values; the external reference is on the grant, and
    /// is left out so that an operator's free text never reaches the audit pointer.
    /// </summary>
    public static string AuditReference(Guid grantId, CreditGrantSource source, string field, decimal credits) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"grantId={grantId:D};source={source};{field}={credits:0.####}");
}

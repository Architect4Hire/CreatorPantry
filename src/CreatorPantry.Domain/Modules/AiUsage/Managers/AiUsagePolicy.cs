namespace CreatorPantry.Domain.Modules.AiUsage.Managers;

/// <summary>
/// Limits for the account usage ledger, shared by EF configuration and by the recording seam.
/// </summary>
/// <remarks>
/// <para>
/// The lengths here restate values the AI module already declares on <c>AiPolicy</c> rather than referencing
/// them. That is not an oversight: <c>ModuleBoundaryTests.InternalManagerSuffixes</c> lists <c>Policy</c> among
/// the module-internal manager types that never cross a module boundary, precisely so one module cannot pin
/// its schema to another's constants. The duplication is the cost of that rule, and it is a cheap one — these
/// are the widths a provider identifier is stored at, not a shared invariant.
/// </para>
/// <para>
/// There is deliberately no free-text limit in this class, because there is no free-text column to limit. See
/// <see cref="Data.Entities.AccountAiUsageEntry"/>.
/// </para>
/// </remarks>
public static class AiUsagePolicy
{
    /// <summary>
    /// The Identity account id. Matches the Identity primary key column length, the same way
    /// <c>WorkspaceMembership.UserId</c> does.
    /// </summary>
    public const int AccountIdMaxLength = 450;

    /// <summary>
    /// A stable identifier a provider, model, or deployment is known by. Same width the AI module's execution
    /// record stores one at; see this type's remarks for why it is restated rather than referenced.
    /// </summary>
    public const int ProviderIdentifierMaxLength = 200;

    /// <summary>
    /// An IANA time zone identifier, validated through <c>ITimeZoneConverter.IsValidZone</c> before it is
    /// stored. Comfortably above the longest zone in the tzdb.
    /// </summary>
    public const int TimeZoneIdMaxLength = 100;

    /// <summary>
    /// Precision for an allowance and for a period's running totals.
    /// </summary>
    /// <remarks>
    /// Four decimal places rather than the ledger's six: this counts allowance units, which a creator reads
    /// and an administrator sets, not a fractional currency estimate. The scale exists so that a settlement
    /// converting tokens into credits does not have to round on every attempt and accumulate the error.
    /// </remarks>
    public const int AllowancePrecision = 18;

    /// <inheritdoc cref="AllowancePrecision"/>
    public const int AllowanceScale = 4;

    /// <summary>
    /// The most closed periods one read of an account's history returns.
    /// </summary>
    /// <remarks>
    /// Sixty, which is five years of monthly periods and two months of daily ones. A ceiling rather than a
    /// cursor: api-contract.md reserves cursors for large or changing datasets, and a closed period never
    /// changes. If daily periods become common this is the number that has to be revisited, and the contract
    /// is additive either way — a cursor can be introduced beside the limit without breaking a caller.
    /// </remarks>
    public const int HistoryMaxPeriods = 60;

    /// <summary>How many closed periods a history read returns when the caller does not say.</summary>
    public const int HistoryDefaultPeriods = 12;

    /// <summary>
    /// The highest day of the month a monthly period may anchor to.
    /// </summary>
    /// <remarks>
    /// 28, so every month has the anchor day. See <see cref="AiQuotaPeriodLength.Monthly"/> for why a clamping
    /// rule was rejected in favour of a cap.
    /// </remarks>
    public const int MonthlyAnchorMax = 28;
}

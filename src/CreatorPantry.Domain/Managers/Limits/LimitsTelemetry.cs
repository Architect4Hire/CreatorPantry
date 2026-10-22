using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace CreatorPantry.Domain.Managers.Limits;

/// <summary>
/// The metric every limit decision is counted on (15.3): <c>creatorpantry.limits.decisions</c>, tagged with the
/// operation, the kind of limit, the scope and the outcome.
/// </summary>
/// <remarks>
/// <strong>No identifiers in tags.</strong> A workspace or account id would make a series per tenant — unbounded,
/// and a record of who uses what in the metrics backend. Which workspace was refused belongs in a log line with
/// the request's trace, not here.
/// </remarks>
public static class LimitsTelemetry
{
    public const string MeterName = "CreatorPantry.Limits";

    private static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> Decisions = Meter.CreateCounter<long>(
        "creatorpantry.limits.decisions",
        unit: "{decision}",
        description: "Limit checks, by operation, kind, scope and outcome.");

    public static void Admitted(LimitedOperation operation, string kind, LimitScope scope) =>
        Record(operation, kind, scope, "admitted");

    public static void Refused(LimitedOperation operation, string kind, LimitScope scope) =>
        Record(operation, kind, scope, "refused");

    /// <summary>A shared store could not be asked and the instance's own was used instead.</summary>
    public static void FellBack(LimitedOperation operation, LimitScope scope) =>
        Record(operation, "rate", scope, "fallback");

    private static void Record(LimitedOperation operation, string kind, LimitScope scope, string outcome) =>
        Decisions.Add(
            1,
            new TagList
            {
                { "operation", operation.ToString() },
                { "kind", kind },
                { "scope", scope.ToString() },
                { "outcome", outcome },
            });
}

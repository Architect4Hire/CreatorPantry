using System.Diagnostics.Metrics;

namespace CreatorPantry.Domain.Modules.AiUsage.Managers;

/// <summary>
/// The metrics the image spend breaker is alerted on (AP.2.7): what was admitted, in dollars, how many requests
/// it refused, and how many times it tripped. Collected by ServiceDefaults' <c>CreatorPantry.*</c> meter rule.
/// </summary>
/// <remarks>
/// <strong>No identifiers in tags</strong>, for <c>LimitsTelemetry</c>'s reason: an account or workspace id
/// would make a series per tenant and a record of who uses what in the metrics backend. Who was refused belongs
/// in a log line with the request's trace; the trip itself is in <c>PlatformAuditLog</c>.
/// </remarks>
public static class PlatformImageSpendTelemetry
{
    public const string MeterName = "CreatorPantry.AiUsage";

    private static readonly Meter Meter = new(MeterName);

    private static readonly Counter<double> Admitted = Meter.CreateCounter<double>(
        "creatorpantry.ai.image_spend.admitted",
        unit: "USD",
        description: "Estimated list-price spend of the pictures admitted, platform-wide.");

    private static readonly Counter<long> Refused = Meter.CreateCounter<long>(
        "creatorpantry.ai.image_spend.refused",
        unit: "{request}",
        description: "Picture requests refused because the platform's daily image spend ceiling was reached.");

    private static readonly Counter<long> Trips = Meter.CreateCounter<long>(
        "creatorpantry.ai.image_spend.tripped",
        unit: "{trip}",
        description: "Times the daily image spend breaker tripped: at most once per UTC day.");

    public static void RecordAdmitted(long micros) =>
        Admitted.Add((double)PlatformImageSpendPolicy.ToDollars(micros));

    public static void RecordRefused() => Refused.Add(1);

    public static void RecordTripped() => Trips.Add(1);
}

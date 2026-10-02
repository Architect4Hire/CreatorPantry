namespace CreatorPantry.Domain.Modules.Brand.Managers;

/// <summary>
/// The caller's own "Create my voice" setup session as the application publishes it.
/// </summary>
/// <param name="Status">`inProgress` or `completed` (see <see cref="BrandSetupSessionStatuses"/>).</param>
/// <param name="CurrentStep">The step slug the wizard is on.</param>
/// <param name="FurthestStep">The furthest step slug reached.</param>
/// <param name="CompletedSteps">Steps finished, in wizard order.</param>
/// <param name="SkippedSteps">Steps skipped, in wizard order.</param>
/// <param name="DraftJson">The opaque draft, a JSON object as text, exactly as saved.</param>
/// <param name="CreatedUtc">When the session started.</param>
/// <param name="UpdatedUtc">When it last changed.</param>
/// <param name="CompletedUtc">When it was completed, or null.</param>
/// <param name="RowVersion">Opaque concurrency token; quote it as <c>If-Match</c> on the next write.</param>
/// <remarks>Carries no workspace or user identifier.</remarks>
public sealed record BrandSetupSessionServiceModel(
    string Status,
    string CurrentStep,
    string FurthestStep,
    IReadOnlyList<string> CompletedSteps,
    IReadOnlyList<string> SkippedSteps,
    string DraftJson,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    DateTimeOffset? CompletedUtc,
    string RowVersion);

/// <summary>The wire spellings of <see cref="BrandSetupSessionStatus"/>, camelCase by contract.</summary>
public static class BrandSetupSessionStatuses
{
    public const string InProgress = "inProgress";

    public const string Completed = "completed";

    public static string ToWire(BrandSetupSessionStatus status) => status switch
    {
        BrandSetupSessionStatus.InProgress => InProgress,
        BrandSetupSessionStatus.Completed => Completed,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };
}

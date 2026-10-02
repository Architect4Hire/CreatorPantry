namespace CreatorPantry.Domain.Modules.Brand.Managers;

/// <summary>
/// Saves the caller's "Create my voice" setup session, creating it on first save. Carries no workspace, user,
/// status or timestamp: the workspace comes from the route, the user from the authenticated caller, and the
/// concurrency token from the <c>If-Match</c> header.
/// </summary>
public sealed record SaveBrandSetupSessionViewModel
{
    public string? CurrentStep { get; init; }

    public string? FurthestStep { get; init; }

    public IReadOnlyList<string?>? CompletedSteps { get; init; }

    public IReadOnlyList<string?>? SkippedSteps { get; init; }

    /// <summary>
    /// An opaque JSON object holding the wizard's in-progress draft. At most 64 KB. Stored verbatim, never
    /// interpreted, never logged, and never a source of truth: style guide versions are.
    /// </summary>
    public string? DraftJson { get; init; }
}

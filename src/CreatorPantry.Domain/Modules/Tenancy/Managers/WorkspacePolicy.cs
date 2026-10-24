using CreatorPantry.Domain.Modules.Measurement.Managers;

namespace CreatorPantry.Domain.Modules.Tenancy.Managers;

/// <summary>Workspace and membership limits shared by request validation and EF configuration.</summary>
public static class WorkspacePolicy
{
    public const int NameMaxLength = 100;

    /// <summary>Lowercase letters, digits, and hyphens; matches the route segment used to resolve a workspace.</summary>
    public const int SlugMaxLength = 100;

    /// <summary>Lowercase alphanumeric segments joined by single hyphens; no leading, trailing, or doubled hyphen.</summary>
    public const string SlugPattern = "^[a-z0-9]+(-[a-z0-9]+)*$";

    /// <summary>
    /// First path segments the browser owns as literal routes of its own, so no workspace may have one as its
    /// address.
    /// </summary>
    /// <remarks>
    /// A workspace that took one would be permanently unreachable rather than merely inconvenient: the SPA's
    /// router matches each literal before it reaches <c>:workspaceSlug</c>, so every link into that workspace
    /// would land on the public page, the sign-in page or the shell's own index instead. Nothing would error.
    /// <c>ReservedSlugMirrorTests</c> reads <c>app.routes.ts</c> and fails until a new literal route appears
    /// here, because the drift is otherwise invisible from this side of the repository.
    /// </remarks>
    public static readonly IReadOnlySet<string> ReservedSlugs = new HashSet<string>(StringComparer.Ordinal)
    {
        "app", "confirm-email", "design-system", "pricing", "sign-in", "sign-up",
    };

    /// <summary>Whether <paramref name="slug"/> is an address the browser already serves something else at.</summary>
    public static bool IsReservedSlug(string slug) => ReservedSlugs.Contains(slug);

    /// <summary>
    /// What a workspace works in until its owner says otherwise, and what every workspace that existed before
    /// the setting did is recorded as.
    /// </summary>
    public const MeasurementSystem InitialMeasurementSystem = MeasurementSystem.UsCustomary;

    /// <summary>
    /// The systems a workspace may default to (B-08). <see cref="MeasurementSystem.Neutral"/> is not a system a
    /// recipe can be written in, and imperial is deliberately not offered: its fluid measures share names with
    /// the US ones at different magnitudes, and nothing downstream distinguishes the two for a creator yet.
    /// </summary>
    public static bool IsSelectableMeasurementSystem(MeasurementSystem system) =>
        system is MeasurementSystem.Metric or MeasurementSystem.UsCustomary;
}

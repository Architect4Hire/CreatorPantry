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

using CreatorPantry.Domain.Modules.Measurement.Managers;

namespace CreatorPantry.Domain.Modules.Tenancy.Managers;

/// <summary>
/// Sets the resolved workspace's default measurement system (B-08). Nullable so that a body which omits the
/// field is a field error rather than a silent <see cref="MeasurementSystem.Neutral"/>.
/// </summary>
public sealed record SetMeasurementPreferenceViewModel(MeasurementSystem? DefaultMeasurementSystem);

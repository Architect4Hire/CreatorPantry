using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Measurement.Managers;

namespace CreatorPantry.Domain.Modules.Tenancy.Managers;

/// <summary>A workspace plus the caller's own membership in it.</summary>
/// <param name="DefaultMeasurementSystem">
/// The system this workspace works in by default (B-08): always metric or US customary.
/// </param>
public sealed record WorkspaceServiceModel(
    Guid WorkspaceId,
    string Name,
    string Slug,
    DateTimeOffset CreatedAt,
    Guid MembershipId,
    WorkspaceRole Role,
    MeasurementSystem DefaultMeasurementSystem);

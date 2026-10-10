using CreatorPantry.Domain.Modules.Measurement.Managers;
using CreatorPantry.Domain.Modules.Tenancy.Managers;

namespace CreatorPantry.Domain.Modules.Tenancy.Data.Entities;

/// <summary>
/// A creator's tenant: the root of every workspace-owned entity. Not itself workspace-scoped.
/// </summary>
public class Workspace
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Lowercase, kebab-case route identifier. Unique across the platform.</summary>
    public string Slug { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// The measurement system this workspace works in by default (B-08): metric or US customary, never
    /// <see cref="MeasurementSystem.Neutral"/> or <see cref="MeasurementSystem.Imperial"/>. A preference for
    /// what is written or shown next — it never rewrites a line a creator entered.
    /// </summary>
    public MeasurementSystem DefaultMeasurementSystem { get; set; } = WorkspacePolicy.InitialMeasurementSystem;
}

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
}

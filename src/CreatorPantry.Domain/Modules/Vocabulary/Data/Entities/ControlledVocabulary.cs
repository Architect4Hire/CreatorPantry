namespace CreatorPantry.Domain.Modules.Vocabulary.Data.Entities;

/// <summary>
/// The shape every controlled vocabulary in the platform catalogue shares: a permanent machine key, a name
/// to show, and whether it is still offered. Global reference data: no <c>WorkspaceId</c>, not
/// <see cref="Tenancy.IWorkspaceOwned"/>, readable before a workspace is resolved (tenancy.md).
/// </summary>
/// <remarks>
/// <para>
/// A base class for the shared rules, not a mapped entity: it has no <c>DbSet</c> and no configuration of
/// its own, so EF Core never sees it as an entity type and each vocabulary below gets its own table rather
/// than a shared one with a discriminator. Keeping them separate is the point — a cuisine and a piece of
/// equipment answer different questions and are referenced by different recipe fields.
/// </para>
/// <para>
/// <see cref="FoodCategory"/> predates this base and keeps its own declaration; it is the ingredient
/// catalogue's grouping rather than recipe metadata, and rewriting a shipped table to inherit here would
/// churn a migration for no behavior change.
/// </para>
/// </remarks>
public abstract class ControlledVocabulary
{
    public Guid Id { get; set; }

    /// <summary>
    /// Stable lowercase machine key, e.g. <c>tex-mex</c>. Permanent: seed data resolves a row by it, and
    /// later recipe rows and saved filters will too, so changing one silently re-points existing data.
    /// </summary>
    public string Code { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Whether the entry is offered for new input. A retired entry stays readable so recipes that already
    /// reference it keep resolving; it simply leaves the pickers.
    /// </summary>
    public bool IsActive { get; set; } = true;
}

using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Nutrition.Managers;

namespace CreatorPantry.Domain.Modules.Nutrition.Data.Entities;

/// <summary>
/// One workspace's decision about which external nutrition reference a platform ingredient is (14.2, B-34).
/// Workspace-owned.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Per ingredient, not per recipe line.</strong> A creator confirms "all-purpose flour is FDC 169761" once,
/// and every recipe line in the workspace that names that ingredient uses it. Line ids are not stable across
/// edits, and a mapping keyed on them would be lost by moving a line between groups.
/// </para>
/// <para>
/// <strong>It never touches a recipe.</strong> Nothing here writes to a line, its display text or its
/// ingredient reference; a line with no platform ingredient has nothing to map, and an estimate reports it as
/// unmapped (14.3).
/// </para>
/// <para>
/// <strong>The workspace's own.</strong> A confirmation, a rejection and a creator's portion are this
/// workspace's decisions about its own recipes. Nothing reads them from another workspace, and nothing derives a
/// shared or platform mapping from them.
/// </para>
/// </remarks>
public class NutritionMapping : IWorkspaceOwned
{
    public Guid Id { get; set; }

    /// <summary>Stamped by <c>WorkspaceOwnershipInterceptor</c> from the resolved context, never by hand.</summary>
    public Guid WorkspaceId { get; set; }

    /// <summary>The platform ingredient this decision is about. One mapping per ingredient per workspace.</summary>
    public Guid IngredientId { get; set; }

    public NutritionMappingStatus Status { get; set; }

    /// <summary>How the reference was chosen. Null exactly when there is no reference.</summary>
    public NutritionMatchMethod? MatchMethod { get; set; }

    /// <summary>For an automatic proposal only.</summary>
    public NutritionMatchConfidence? Confidence { get; set; }

    /// <summary>The cited source. Null exactly when <see cref="Status"/> is <see cref="NutritionMappingStatus.Unresolved"/>.</summary>
    public Guid? ReferenceSourceId { get; set; }

    /// <summary>The source's permanent code, kept beside its id so the source can be asked again without a lookup.</summary>
    public string? SourceCode { get; set; }

    /// <summary>The source's own id for the record. Here, and never on a recipe or ingredient.</summary>
    public string? ExternalRecordId { get; set; }

    /// <summary>The source's own name for the food, kept so the mapping can be read without asking the source.</summary>
    public string? ReferenceDescription { get; set; }

    /// <summary>The data set and publication date of the record as it was proposed or confirmed.</summary>
    public string? SourceVersion { get; set; }

    /// <summary>The newer version a refresh found. Set with <see cref="NutritionMappingStatus.NeedsReview"/> and only with it.</summary>
    public string? ReviewSourceVersion { get; set; }

    /// <summary>Set with <see cref="NutritionMappingStatus.Unresolved"/> and only with it.</summary>
    public NutritionUnresolvedReason? UnresolvedReason { get; set; }

    /// <summary>
    /// The unit of the one portion that turns a volume or count quantity into grams. Nutrient amounts are per
    /// 100 g, so a line in a mass unit needs no portion. The portion is whole or absent.
    /// </summary>
    public Guid? PortionMeasurementUnitId { get; set; }

    public decimal? PortionAmount { get; set; }

    public decimal? PortionGrams { get; set; }

    /// <summary>The source's words for a reference portion; null for a creator's.</summary>
    public string? PortionLabel { get; set; }

    public NutritionPortionOrigin? PortionOrigin { get; set; }

    /// <summary>When a creator confirmed the reference. Kept through <see cref="NutritionMappingStatus.NeedsReview"/>.</summary>
    public DateTimeOffset? ConfirmedAt { get; set; }

    public Guid? ConfirmedByMembershipId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid CreatedByMembershipId { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid UpdatedByMembershipId { get; set; }

    public byte[] RowVersion { get; set; } = [];

    public List<NutritionMappingRejection> Rejections { get; set; } = [];
}

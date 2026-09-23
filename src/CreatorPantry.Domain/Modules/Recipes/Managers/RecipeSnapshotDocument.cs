using CreatorPantry.Domain.Managers.Reference;

namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// The complete content of one recipe at one moment, as a structured document rather than an opaque blob.
/// This is what a <c>RecipeVersion</c> stores, what a diff compares, and what a restore reads.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why a document and not shadow tables.</strong> A parallel set of <c>RecipeVersion*</c> tables
/// would be coupled to the live schema forever: add a column to <c>RecipeIngredient</c> and it must be added
/// to the shadow too, where every historical row then carries a <c>NULL</c> that cannot be told apart from a
/// field the creator genuinely left blank. The snapshot would start describing the past inaccurately. A
/// self-describing document carrying its own <see cref="SchemaVersion"/> records what was actually true when
/// it was written, and a reader knows how to interpret it before it touches a field.
/// </para>
/// <para>
/// <strong>Why this is serialized explicitly rather than mapped with EF's <c>ToJson()</c>.</strong> EF's
/// owned-entity JSON mapping deserializes stored rows under the <em>current</em> model, so a property added
/// next year would silently appear on every historical snapshot with a default value — exactly the failure
/// the shadow tables were rejected for, arriving through a different door. Owning the serialization means a
/// reader can branch on <see cref="SchemaVersion"/> instead — EF does not get authority over what an old
/// snapshot means. The column itself is an ordinary <c>nvarchar(max)</c>; see
/// <c>RecipeVersionSnapshotConfiguration</c> for why the native <c>json</c> type was weighed and declined.
/// </para>
/// <para>
/// <strong>Stable ids are carried throughout</strong> — every group, line and step keeps the identifier it
/// had in the live aggregate. That is what lets a diff match a moved ingredient to itself rather than
/// reporting a deletion and an insertion, and what lets a restore put content back where it came from.
/// </para>
/// </remarks>
public sealed record RecipeSnapshotDocument
{
    /// <summary>
    /// The oldest shape this build can still read. Every version from here to
    /// <see cref="CurrentSchemaVersion"/> must remain readable: a snapshot is an archive, and raising this
    /// number abandons creator history that is already written.
    /// </summary>
    public const int MinimumReadableSchemaVersion = 1;

    /// <summary>
    /// The shape written today. Increment when a change would make an older document read incorrectly under
    /// the new records; never renumber, and never reuse.
    /// </summary>
    /// <remarks>
    /// Incrementing this is only half the work. The other half is teaching <c>RecipeSnapshotMapper</c> to
    /// read every version from <see cref="MinimumReadableSchemaVersion"/> upward — otherwise the bump
    /// silently turns every snapshot already in the database into an unreadable row, which is precisely the
    /// outcome storing a self-describing document was chosen to avoid.
    /// </remarks>
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// Which shape this document was written in. Defaults to <c>0</c> — not to
    /// <see cref="CurrentSchemaVersion"/> — so that a stored document with no version, a truncated row, or
    /// one produced by something that is not this serializer is rejected rather than silently read as the
    /// current shape. An unversioned document is exactly the case the version check exists to catch, and a
    /// defaulting initializer here would let it through.
    /// </summary>
    public int SchemaVersion { get; init; }

    public required RecipeSnapshotHeader Recipe { get; init; }

    public IReadOnlyList<RecipeSnapshotIngredientGroup> IngredientGroups { get; init; } = [];

    public IReadOnlyList<RecipeSnapshotInstructionGroup> InstructionGroups { get; init; } = [];

    public IReadOnlyList<RecipeSnapshotEquipment> Equipment { get; init; } = [];

    public IReadOnlyList<RecipeSnapshotAssetLink> AssetLinks { get; init; } = [];

    /// <summary>
    /// The creator's tags at capture time, as vocabulary references.
    /// </summary>
    /// <remarks>
    /// Ids rather than names, consistently with how <see cref="RecipeSnapshotHeader.CuisineId"/> and the rest
    /// of the vocabulary references are stored. The consequence to know: renaming a tag changes what a
    /// restored snapshot displays, because the archive records which tag was applied, not what it was called
    /// that day. That is the same trade every other vocabulary reference here makes, and the alternative —
    /// freezing display names into the archive — would make a rename fail to propagate instead.
    /// </remarks>
    public IReadOnlyList<RecipeSnapshotTag> Tags { get; init; } = [];
}

/// <summary>The recipe's own fields, excluding identity, ownership, and audit columns.</summary>
/// <remarks>
/// <c>Id</c>, <c>WorkspaceId</c>, <c>CreatedAt</c>, <c>RowVersion</c> and the membership columns are
/// deliberately absent: they belong to the live row and to the version's own metadata, not to the content
/// being versioned. Putting them here would mean a restore could change who owns a recipe.
/// </remarks>
public sealed record RecipeSnapshotHeader
{
    public required string Title { get; init; }

    public string? Description { get; init; }

    public string? Headnote { get; init; }

    public string? Notes { get; init; }

    public string? StorageNotes { get; init; }

    public string? AttributionText { get; init; }

    public string? SourceUrl { get; init; }

    public Guid? CuisineId { get; init; }

    public Guid? CourseId { get; init; }

    public Guid? PrimaryTechniqueId { get; init; }

    public int? PrepTimeMinutes { get; init; }

    public int? CookTimeMinutes { get; init; }

    public int? RestTimeMinutes { get; init; }

    public int? TotalTimeMinutes { get; init; }

    public string? YieldText { get; init; }

    public decimal? YieldQuantity { get; init; }

    public Guid? YieldUnitId { get; init; }

    public MeasurementDimension? YieldUnitDimension { get; init; }

    public RecipeStatus Status { get; init; }
}

public sealed record RecipeSnapshotIngredientGroup
{
    public required Guid Id { get; init; }

    public string? Title { get; init; }

    public int SortOrder { get; init; }

    public IReadOnlyList<RecipeSnapshotIngredient> Ingredients { get; init; } = [];
}

/// <summary>
/// One ingredient line. <see cref="DisplayText"/> is the creator's wording and is what a restore puts back;
/// every other field is the additive reading of it that recipes.md keeps subordinate to the text.
/// </summary>
public sealed record RecipeSnapshotIngredient
{
    public required Guid Id { get; init; }

    public int SortOrder { get; init; }

    public required string DisplayText { get; init; }

    public string? IngredientNameText { get; init; }

    public decimal? Quantity { get; init; }

    public decimal? QuantityUpper { get; init; }

    public Guid? MeasurementUnitId { get; init; }

    public MeasurementDimension? MeasurementUnitDimension { get; init; }

    public Guid? IngredientId { get; init; }

    public IngredientMatchStatus MatchStatus { get; init; }

    public string? PreparationNote { get; init; }

    public bool IsOptional { get; init; }

    public IngredientScaling ScalingBehavior { get; init; }
}

public sealed record RecipeSnapshotInstructionGroup
{
    public required Guid Id { get; init; }

    public string? Title { get; init; }

    public int SortOrder { get; init; }

    public IReadOnlyList<RecipeSnapshotInstructionStep> Steps { get; init; } = [];
}

public sealed record RecipeSnapshotInstructionStep
{
    public required Guid Id { get; init; }

    public int SortOrder { get; init; }

    public required string Text { get; init; }

    public Guid? TechniqueId { get; init; }

    public int? DurationMinutes { get; init; }

    public decimal? TemperatureValue { get; init; }

    public Guid? TemperatureUnitId { get; init; }

    public MeasurementDimension? TemperatureUnitDimension { get; init; }

    public string? Note { get; init; }
}

public sealed record RecipeSnapshotEquipment
{
    public required Guid Id { get; init; }

    public int SortOrder { get; init; }

    public required string DisplayText { get; init; }

    public Guid? EquipmentTypeId { get; init; }

    public bool IsOptional { get; init; }

    public string? Note { get; init; }
}

public sealed record RecipeSnapshotAssetLink
{
    public required Guid Id { get; init; }

    public int SortOrder { get; init; }

    /// <summary>
    /// A workspace-owned asset id, carried verbatim and constrained by nothing.
    /// </summary>
    /// <remarks>
    /// <c>RecipeAssetLink.MediaAssetId</c> records that it has no foreign key and that the write seam must
    /// validate it against the resolved workspace. This is a <em>second</em> route to the same storage, and
    /// an easier one to overlook: a restore does not look like client input, so an implementer may not think
    /// of it as untrusted. It is — a document can arrive from an import, and ai.md treats imported content
    /// as untrusted. Restore must revalidate this id against the resolved workspace exactly as create does,
    /// or workspace A ends up with a row pointing at workspace B's photograph.
    /// </remarks>
    public Guid MediaAssetId { get; init; }

    public RecipeAssetRole Role { get; init; }

    public string? Caption { get; init; }
}

/// <summary>One tag applied to the recipe, as a reference into the workspace's tag vocabulary.</summary>
public sealed record RecipeSnapshotTag
{
    public required Guid WorkspaceTagId { get; init; }
}

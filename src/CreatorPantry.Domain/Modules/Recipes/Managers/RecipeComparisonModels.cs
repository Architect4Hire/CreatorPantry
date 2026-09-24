namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// What changed between two recipe snapshots, grouped into the sections a creator reads a recipe in.
/// </summary>
/// <remarks>
/// <para>
/// <strong>These types are published on the wire.</strong> This record and everything it contains —
/// <see cref="RecipeComparisonSectionResult"/>, <see cref="RecipeFieldChange"/>,
/// <see cref="RecipeItemChange"/>, <see cref="RecipeItemPresence"/>, <see cref="RecipeComparisonSection"/>
/// and <see cref="RecipeComparisonField"/> — are returned verbatim by
/// <c>GET /api/v1/workspaces/{workspaceSlug}/recipes/{recipeId}/versions/compare</c>, inside
/// <see cref="RecipeVersionComparisonServiceModel"/>, and are bound by api-contract.md's compatibility
/// rules. None of them carries the <c>ServiceModel</c> suffix that normally marks a wire type, because they
/// are <see cref="RecipeComparer"/>'s own output and reusing it is what keeps the contract and the algebra
/// from becoming two descriptions of one edit. Renaming a member here, changing what one means, or removing
/// one is a breaking API change however internal the edit feels.
/// </para>
/// <para>
/// <strong>Produced by <see cref="RecipeComparer"/> and by nothing else.</strong> A diff a caller can
/// construct is a diff a caller can assert, and AIREC-GR-002 exists because a model-supplied <c>before</c>
/// value is exactly the thing that must never be believed. This shape is returned from a pure comparison of
/// two documents the server read for itself; a <see cref="RecipeComparison"/> arriving from anywhere else is
/// unverified.
/// </para>
/// <para>
/// <strong>Every section is always present</strong>, in the order <see cref="RecipeComparisonSection"/>
/// declares, whether or not it changed. A comparison panel then renders one stable frame, and "nothing
/// changed in the timing" is a statement the result makes rather than an absence the reader has to notice.
/// </para>
/// </remarks>
public sealed record RecipeComparison
{
    public required IReadOnlyList<RecipeComparisonSectionResult> Sections { get; init; }

    /// <summary>Whether the two documents differ at all.</summary>
    /// <remarks>
    /// Derived rather than stored, so it cannot disagree with the sections beneath it. Comparing a document
    /// with itself makes this <c>false</c>, which is what lets the diff serve as a "has this actually
    /// changed" check rather than only as something to render.
    /// </remarks>
    public bool HasChanges => Sections.Any(section => section.HasChanges);

    /// <summary>The result for one section, for readers that render sections independently.</summary>
    /// <exception cref="InvalidOperationException">
    /// The section is absent, which a well-formed comparison never is.
    /// </exception>
    public RecipeComparisonSectionResult this[RecipeComparisonSection section] =>
        Sections.Single(result => result.Section == section);
}

/// <summary>One section's changes: the scalar fields it owns, and the items it contains.</summary>
public sealed record RecipeComparisonSectionResult
{
    public required RecipeComparisonSection Section { get; init; }

    /// <summary>
    /// Fields of the recipe itself that this section owns. Unchanged fields are omitted; a section whose
    /// fields all match contributes an empty list, not a list of "unchanged" entries.
    /// </summary>
    public required IReadOnlyList<RecipeFieldChange> FieldChanges { get; init; }

    /// <summary>
    /// Changes to the identified items this section contains — ingredient lines, steps, equipment, asset
    /// links, tags, and the groups above them. Items that were neither edited nor moved are omitted.
    /// </summary>
    public required IReadOnlyList<RecipeItemChange> ItemChanges { get; init; }

    public bool HasChanges => FieldChanges.Count > 0 || ItemChanges.Count > 0;
}

/// <summary>One field that differs, rendered for reading rather than for arithmetic.</summary>
/// <remarks>
/// <para>
/// <strong>Detection and rendering are separate.</strong> Whether a field changed is decided on the typed
/// values, so <c>240</c> and <c>240.0</c> are the same quantity and a document that happened to store a
/// different decimal scale does not read as an edit. Only once a difference is established are the two
/// values rendered, with the invariant culture, into the strings here.
/// </para>
/// <para>
/// <strong>Identifiers stay identifiers.</strong> A cuisine, unit, technique or ingredient reference is
/// rendered as its id, never as a display name: resolving vocabulary needs reads this comparison
/// deliberately does not perform, and doing it here would make a pure function need a database.
/// </para>
/// <para>
/// <c>null</c> means the field held nothing on that side — either because it was empty, or, for an added or
/// removed item, because that side of the comparison does not exist.
/// </para>
/// </remarks>
public sealed record RecipeFieldChange
{
    public required RecipeComparisonField Field { get; init; }

    public required string? From { get; init; }

    public required string? To { get; init; }
}

/// <summary>
/// What happened to one identified item — an ingredient line, a step, a group, a piece of equipment, an
/// asset link, or a tag.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Presence and movement are independent facts, deliberately.</strong> An item that was reworded
/// and dragged reports both; an item that only moved reports <see cref="Moved"/> with no field changes at
/// all. Encoding a move as a replacement of its position would put reordering and rewriting into one
/// vocabulary, and a reviewer reading "this line changed" could no longer tell which happened.
/// </para>
/// <para>
/// <strong>Position is a rank, not a stored <c>SortOrder</c>.</strong> Sort orders may be gapped or
/// renumbered without anything moving, so <see cref="FromRank"/> and <see cref="ToRank"/> count siblings in
/// document order instead. <c>SortOrder</c> itself is never reported as a field change — see
/// <see cref="RecipeComparisonField"/>.
/// </para>
/// </remarks>
public sealed record RecipeItemChange
{
    /// <summary>The item's stable identifier, the same on both sides when it exists on both.</summary>
    public required Guid Id { get; init; }

    public required RecipeItemPresence Presence { get; init; }

    /// <summary>
    /// Whether the item sits somewhere else relative to the items that survived alongside it, or under a
    /// different parent. Always <c>false</c> for an added or removed item, which has nowhere to have moved
    /// from or to.
    /// </summary>
    public required bool Moved { get; init; }

    /// <summary>
    /// The group this item belonged to, or <c>null</c> for an item the recipe holds directly — equipment,
    /// asset links, tags, and the groups themselves.
    /// </summary>
    public required Guid? FromParentId { get; init; }

    /// <inheritdoc cref="FromParentId"/>
    public required Guid? ToParentId { get; init; }

    /// <summary>Its 0-based position among its siblings before the change; <c>null</c> when it was added.</summary>
    public required int? FromRank { get; init; }

    /// <summary>Its 0-based position among its siblings after the change; <c>null</c> when it was removed.</summary>
    public required int? ToRank { get; init; }

    /// <summary>
    /// For a retained item, the fields that differ. For an added or removed item, its content — with
    /// <see cref="RecipeFieldChange.From"/> null on an addition and <see cref="RecipeFieldChange.To"/> null
    /// on a removal — so a reader can render what appeared or disappeared without holding either source
    /// document.
    /// </summary>
    /// <remarks>
    /// An added or removed item states what was written, not what was left unsaid: fields sitting at
    /// "nothing was said here" — an empty value, a false flag, or an enum's zero member such as
    /// <see cref="IngredientMatchStatus.NotAttempted"/> — are omitted. A retained item is compared field by
    /// field with no such suppression, so turning a flag off is reported exactly like turning it on.
    /// </remarks>
    public required IReadOnlyList<RecipeFieldChange> FieldChanges { get; init; }
}

/// <summary>Whether an item exists on one side of the comparison or on both.</summary>
public enum RecipeItemPresence
{
    /// <summary>Present on both sides, matched by its stable identifier.</summary>
    Retained = 0,

    /// <summary>Present only in the later document.</summary>
    Added = 1,

    /// <summary>Present only in the earlier document.</summary>
    Removed = 2,
}

/// <summary>
/// The parts a recipe is read in, and the order a comparison presents them.
/// </summary>
/// <remarks>
/// These partition the snapshot exhaustively: every archived field belongs to exactly one, which is what
/// <c>RecipeComparisonCompletenessTests</c> holds the code to. <see cref="Equipment"/> and
/// <see cref="Media"/> are sections in their own right rather than folded into metadata, because a recipe
/// that lost its hero image or its pan has changed in a way a creator would want to see named.
/// </remarks>
public enum RecipeComparisonSection
{
    /// <summary>Title, description, attribution, and the vocabulary the recipe is filed under.</summary>
    Metadata = 0,

    /// <summary>Prep, cook, rest, and total times.</summary>
    Timing = 1,

    /// <summary>What the recipe makes, as text and as a measured quantity.</summary>
    Yield = 2,

    /// <summary>Headnote, notes, and storage notes — the creator's prose around the recipe.</summary>
    Notes = 3,

    /// <summary>
    /// Editorial state. Named for what a creator calls it, not for a claim about any provider: content.md
    /// is explicit that status does not mean anything was delivered anywhere.
    /// </summary>
    Publication = 4,

    /// <summary>Ingredient groups and the lines within them.</summary>
    Ingredients = 5,

    /// <summary>Instruction groups and the steps within them.</summary>
    Instructions = 6,

    /// <summary>Equipment lines.</summary>
    Equipment = 7,

    /// <summary>Linked media assets.</summary>
    Media = 8,
}

/// <summary>
/// Every field a comparison can report, across the recipe and all of its children.
/// </summary>
/// <remarks>
/// <para>
/// One enum rather than one per record, so <see cref="RecipeFieldChange"/> stays a single shape and a
/// reader switches over a closed set. Each member maps to exactly one property of exactly one
/// <c>RecipeSnapshot*</c> record, and <c>RecipeComparisonCompletenessTests</c> fails if a property is added
/// without a member, or a member exists that the comparer never emits.
/// </para>
/// <para>
/// <strong>These member names are published, and renaming a snapshot property drags one along.</strong>
/// <c>RecipeComparisonCompletenessTests</c> derives the expected member name from the snapshot property's
/// own name, so renaming <c>RecipeSnapshotIngredient.PreparationNote</c> mechanically demands renaming
/// <see cref="IngredientPreparationNote"/> — a breaking change to the wire, produced by a refactor nobody
/// would think of as touching a contract. That test's <c>NamedDifferently</c> table is the lever for exactly
/// this case: add the property there pointing at the existing member, and the wire name stays put while the
/// C# name moves.
/// </para>
/// <para>
/// <strong>This enum grows.</strong> Adding a member is compatible for a client that tolerates unknown
/// values and breaking for a generated client with a closed enum; the former is what the contract asks for.
/// </para>
/// <para>
/// <strong>Two things are absent on purpose.</strong> There is no member for <c>Id</c>, which identifies an
/// item rather than describing it, and none for <c>SortOrder</c>, because position is reported through
/// <see cref="RecipeItemChange.Moved"/> and the ranks beside it. A <c>SortOrder</c> field change would make
/// every reorder look like a handful of replacements, and would fire on a renumbering that moved nothing.
/// </para>
/// </remarks>
public enum RecipeComparisonField
{
    // Metadata.
    Title,
    Description,
    AttributionText,
    SourceUrl,
    CuisineId,
    CourseId,
    PrimaryTechniqueId,
    TagWorkspaceTagId,

    // Timing.
    PrepTimeMinutes,
    CookTimeMinutes,
    RestTimeMinutes,
    TotalTimeMinutes,

    // Yield.
    YieldText,
    YieldQuantity,
    YieldUnitId,
    YieldUnitDimension,

    // Notes.
    Headnote,
    Notes,
    StorageNotes,

    // Publication.
    Status,

    // Ingredients.
    IngredientGroupTitle,
    IngredientDisplayText,
    IngredientNameText,
    IngredientQuantity,
    IngredientQuantityUpper,
    IngredientMeasurementUnitId,
    IngredientMeasurementUnitDimension,

    /// <summary>The matched platform ingredient, which enriches the line and never replaces its text.</summary>
    IngredientReferenceId,
    IngredientMatchStatus,
    IngredientPreparationNote,
    IngredientIsOptional,
    IngredientScalingBehavior,

    // Instructions.
    InstructionGroupTitle,
    StepText,
    StepTechniqueId,
    StepDurationMinutes,
    StepTemperatureValue,
    StepTemperatureUnitId,
    StepTemperatureUnitDimension,
    StepNote,

    // Equipment.
    EquipmentDisplayText,
    EquipmentTypeId,
    EquipmentIsOptional,
    EquipmentNote,

    // Media.
    AssetMediaAssetId,
    AssetRole,
    AssetCaption,
}

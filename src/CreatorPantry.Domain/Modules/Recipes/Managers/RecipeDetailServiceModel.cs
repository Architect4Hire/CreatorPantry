namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// One recipe as a client reads it: the creator's own text, the structured content beneath it, the metadata
/// of its most recent version, and the token an edit must quote.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Deliberately shaped like <see cref="RecipeSnapshotDocument"/>.</strong> Same nesting, same stable
/// child ids, same explicit <c>SortOrder</c> on every ordered child. The two are the only structured
/// representations of a recipe this system has, and a client that renders a detail view and a client that
/// renders a version diff are the same client. Letting the shapes drift would make it hold two mental models
/// of one recipe.
/// </para>
/// <para>
/// <strong>Vocabulary references are ids, not names.</strong> Cuisine, course, technique, measurement units,
/// equipment types and matched ingredients all belong to other modules, whose display names are already
/// published by <c>/api/v1/reference/*</c> — the same lists an editor has to load anyway. Resolving them
/// here would mean this module reaching into three others on every read to produce strings the client
/// usually already holds. Names can be added later as optional fields, which api-contract.md treats as a
/// compatible change; removing them afterwards would not be.
/// </para>
/// <para>
/// <strong>What is withheld, and why.</strong> <c>WorkspaceId</c> and the membership columns never leave the
/// server: the request cannot name them and the reply does not hand them back. The raw <c>RowVersion</c>
/// bytes are published only as <see cref="ConcurrencyToken"/>. The three denormalized <c>*Dimension</c>
/// columns are absent because they exist to carry a composite foreign key, not to inform a client — each is
/// a fact about the unit, readable from the unit itself. The version's lineage
/// (<c>ParentVersionId</c>, <c>BasedOnRecipeRowVersion</c>), its <c>AiProposalId</c>, its
/// <c>SnapshotSchemaVersion</c> and its snapshot document all belong to the history and diff seams rather
/// than to reading the recipe as it stands.
/// </para>
/// <para>
/// <strong>Every property is <c>required</c>, nullable ones included, and that is a contract decision rather
/// than a C# habit.</strong> The serializer writes nulls, so each of these is present in every response — and
/// the published schema's <c>required</c> set is generated from exactly this modifier. Leaving it off a field
/// the server always writes would document it as optional, and a generated client would then carry a
/// null-check for a value that never fails to arrive. Sibling ServiceModels are positional records, which get
/// this for free; written with initializers instead, for the sake of documenting twenty-eight fields
/// individually, it has to be stated. <em>Required is about presence, not about having a value:</em> a
/// <c>required string?</c> is always sent and may be <c>null</c>.
/// </para>
/// </remarks>
public sealed record RecipeDetailServiceModel
{
    public required Guid Id { get; init; }

    /// <summary>The creator's working title, exactly as entered.</summary>
    public required string Title { get; init; }

    public required string? Description { get; init; }

    public required string? Headnote { get; init; }

    public required string? Notes { get; init; }

    public required string? StorageNotes { get; init; }

    public required string? AttributionText { get; init; }

    public required string? SourceUrl { get; init; }

    public required Guid? CuisineId { get; init; }

    public required Guid? CourseId { get; init; }

    public required Guid? PrimaryTechniqueId { get; init; }

    public required int? PrepTimeMinutes { get; init; }

    public required int? CookTimeMinutes { get; init; }

    public required int? RestTimeMinutes { get; init; }

    public required int? TotalTimeMinutes { get; init; }

    public required string? YieldText { get; init; }

    public required decimal? YieldQuantity { get; init; }

    public required Guid? YieldUnitId { get; init; }

    /// <summary>The creator's editorial state. It implies nothing about external publication.</summary>
    public required RecipeStatus Status { get; init; }

    /// <summary>
    /// Where this recipe was copied from, when it was created by duplicating another; <c>null</c> for a
    /// recipe someone wrote.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the attribution REC-005 asks for, and it is an object rather than a bare version id because a
    /// bare id is not attribution: nothing else on this API resolves a version id to the recipe that owns it,
    /// so a client holding only the id could say "copied from something" and no more. One nullable object
    /// whose presence means "this is a copy" also beats four parallel nullable fields that can only ever be
    /// null together.
    /// </para>
    /// <para>
    /// The server resolves it from the one column it stores, so nothing here can disagree with anything —
    /// see <see cref="RecipeDuplicateSourceRecord"/> for why the source recipe is not stored beside it, and
    /// why the title is the source's current one.
    /// </para>
    /// </remarks>
    public required RecipeDuplicateSourceServiceModel? DuplicatedFrom { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }

    /// <summary>
    /// The value an edit must quote to prove it was composed against this state of the recipe.
    /// </summary>
    /// <remarks>
    /// Opaque on purpose: base64 of the server-generated row version, published under a name that says what
    /// it is for rather than what it is made of. A client stores it and sends it back; it never parses it,
    /// compares it or orders by it, and the representation can change without the contract changing. Handing
    /// it out is this read's job — without it an update seam has nothing to check against, and its only
    /// remaining option is last-write-wins.
    /// </remarks>
    public required string ConcurrencyToken { get; init; }

    /// <summary>
    /// The most recent version of this recipe, or <c>null</c> when it has no history yet.
    /// </summary>
    /// <remarks>
    /// Nullable because the type cannot promise otherwise. Every recipe created through this API gets version
    /// 1 in the same transaction as the recipe, so in practice this is populated — but a read that threw, or
    /// invented a version, on a recipe whose history was somehow absent would turn missing metadata into a
    /// failure to show the creator their own content.
    /// </remarks>
    public required RecipeVersionSummaryServiceModel? CurrentVersion { get; init; }

    /// <summary>Ingredient groups in their creator-defined order, each holding its own ordered lines.</summary>
    public required IReadOnlyList<RecipeIngredientGroupServiceModel> IngredientGroups { get; init; }

    /// <summary>Instruction groups in their creator-defined order, each holding its own ordered steps.</summary>
    public required IReadOnlyList<RecipeInstructionGroupServiceModel> InstructionGroups { get; init; }

    public required IReadOnlyList<RecipeEquipmentServiceModel> Equipment { get; init; }

    public required IReadOnlyList<RecipeAssetLinkServiceModel> AssetLinks { get; init; }

    /// <summary>
    /// The creator's tags, ordered by name. Tags are a set and carry no creator-defined order; the ordering is
    /// the read's own, so that two reads of one recipe do not reshuffle them.
    /// </summary>
    public required IReadOnlyList<RecipeTagServiceModel> Tags { get; init; }
}

/// <summary>
/// The recipe and version a copy was duplicated from — enough to name it and navigate to it.
/// </summary>
/// <remarks>
/// <para>
/// Nothing about what the source <em>said</em> is here. A copy holds its own content, archived at the moment
/// it was made; the source has moved on since, and publishing any of its content beside this would invite a
/// client to render the two as one thing.
/// </para>
/// <para>
/// <see cref="RecipeId"/> is the point of the whole object: it is what lets an interface link to the source
/// rather than merely mention it. It is safe to publish because lineage cannot cross a workspace — the
/// composite foreign key makes that unrepresentable — so a recipe named here is always one the caller may
/// already read.
/// </para>
/// </remarks>
public sealed record RecipeDuplicateSourceServiceModel
{
    /// <summary>The source recipe. Readable by anyone who can read the copy: lineage never leaves a workspace.</summary>
    public required Guid RecipeId { get; init; }

    /// <summary>The source recipe's title as it stands now, not as it was when the copy was made.</summary>
    public required string RecipeTitle { get; init; }

    /// <summary>The exact version whose content was copied.</summary>
    public required Guid VersionId { get; init; }

    /// <summary>That version's number, as the source recipe's history lists it.</summary>
    public required int VersionNumber { get; init; }
}

/// <summary>
/// What the recipe's current version says about itself. Metadata only, never its snapshot document.
/// </summary>
public sealed record RecipeVersionSummaryServiceModel
{
    public required Guid Id { get; init; }

    /// <summary>The number creators cite. Gap-free and unique within the recipe.</summary>
    public required int VersionNumber { get; init; }

    public required RecipeVersionSource Source { get; init; }

    public required RecipeVersionReadiness Readiness { get; init; }

    /// <summary>Why this version was written, when the writer gave a reason. A routine save has none.</summary>
    public required string? Reason { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}

public sealed record RecipeIngredientGroupServiceModel
{
    public required Guid Id { get; init; }

    /// <summary>The creator's heading for this group, such as "For the streusel". Ungrouped lines have none.</summary>
    public required string? Title { get; init; }

    /// <summary>
    /// The group's position. The list is already sorted; this is published so a client editing the order can
    /// state where something is rather than infer it from an array index it may have filtered or reordered.
    /// </summary>
    public required int SortOrder { get; init; }

    public required IReadOnlyList<RecipeIngredientServiceModel> Ingredients { get; init; }
}

/// <summary>
/// One ingredient line. <see cref="DisplayText"/> is what the creator typed and what a client renders;
/// everything else is the additive reading of it that recipes.md keeps subordinate to the text.
/// </summary>
public sealed record RecipeIngredientServiceModel
{
    public required Guid Id { get; init; }

    /// <inheritdoc cref="RecipeIngredientGroupServiceModel.SortOrder"/>
    public required int SortOrder { get; init; }

    /// <summary>The creator's own wording for the whole line, exactly as entered.</summary>
    public required string DisplayText { get; init; }

    /// <summary>The ingredient name as parsed out of the line, when it was parsed. Not a replacement for it.</summary>
    public required string? IngredientNameText { get; init; }

    public required decimal? Quantity { get; init; }

    /// <summary>The upper bound when the creator gave a range ("2 to 3 cups").</summary>
    public required decimal? QuantityUpper { get; init; }

    public required Guid? MeasurementUnitId { get; init; }

    /// <summary>The recognized platform ingredient this line was matched to, when one was.</summary>
    public required Guid? IngredientId { get; init; }

    /// <summary>
    /// How far normalization got with this line. Published because "we did not try" and "we tried and found
    /// nothing" are different facts to a creator, and a null <see cref="IngredientId"/> alone conflates them.
    /// </summary>
    public required IngredientMatchStatus MatchStatus { get; init; }

    public required string? PreparationNote { get; init; }

    public required bool IsOptional { get; init; }

    /// <summary>
    /// Whether this line scales with the recipe. Published so a client can show that "salt, to taste" will
    /// not be multiplied — the scaling itself stays in deterministic domain code (recipes.md).
    /// </summary>
    public required IngredientScaling ScalingBehavior { get; init; }
}

public sealed record RecipeInstructionGroupServiceModel
{
    public required Guid Id { get; init; }

    /// <summary>The creator's heading for this phase, such as "The day before".</summary>
    public required string? Title { get; init; }

    /// <inheritdoc cref="RecipeIngredientGroupServiceModel.SortOrder"/>
    public required int SortOrder { get; init; }

    public required IReadOnlyList<RecipeInstructionStepServiceModel> Steps { get; init; }
}

public sealed record RecipeInstructionStepServiceModel
{
    public required Guid Id { get; init; }

    /// <inheritdoc cref="RecipeIngredientGroupServiceModel.SortOrder"/>
    public required int SortOrder { get; init; }

    /// <summary>The step as the creator wrote it.</summary>
    public required string Text { get; init; }

    public required Guid? TechniqueId { get; init; }

    public required int? DurationMinutes { get; init; }

    public required decimal? TemperatureValue { get; init; }

    public required Guid? TemperatureUnitId { get; init; }

    public required string? Note { get; init; }
}

public sealed record RecipeEquipmentServiceModel
{
    public required Guid Id { get; init; }

    /// <inheritdoc cref="RecipeIngredientGroupServiceModel.SortOrder"/>
    public required int SortOrder { get; init; }

    /// <summary>The creator's wording, such as "9-inch springform tin".</summary>
    public required string DisplayText { get; init; }

    public required Guid? EquipmentTypeId { get; init; }

    public required bool IsOptional { get; init; }

    public required string? Note { get; init; }
}

/// <summary>
/// One media asset attached to the recipe, as a reference rather than as content.
/// </summary>
/// <remarks>
/// <see cref="MediaAssetId"/> and nothing more: no storage URL, no container path, no signed link. Issuing
/// access to bytes is the media seam's decision, made per request with a short lifetime (media.md), and
/// smuggling a durable URL into a recipe read would hand out access that outlives the authorization that
/// justified it.
/// </remarks>
public sealed record RecipeAssetLinkServiceModel
{
    public required Guid Id { get; init; }

    /// <inheritdoc cref="RecipeIngredientGroupServiceModel.SortOrder"/>
    public required int SortOrder { get; init; }

    public required Guid MediaAssetId { get; init; }

    public required RecipeAssetRole Role { get; init; }

    public required string? Caption { get; init; }
}

/// <summary>One tag applied to the recipe, with the name the workspace's vocabulary currently gives it.</summary>
/// <remarks>
/// The name is resolved rather than frozen, so renaming a tag changes what every recipe carrying it displays
/// — the opposite trade from <see cref="RecipeSnapshotTag"/>, which records only the id because an archive
/// must describe what was applied, not what it happens to be called today.
/// </remarks>
public sealed record RecipeTagServiceModel
{
    public required Guid WorkspaceTagId { get; init; }

    public required string Name { get; init; }
}

using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Modules.Recipes.Managers;

namespace CreatorPantry.Domain.Modules.Recipes.Data.Entities;

/// <summary>
/// A creator's recipe: the canonical source for recipe facts and the root of the recipe aggregate.
/// Workspace-owned intellectual property (tenancy.md) — private to one workspace, never shared, never
/// globalised for deduplication.
/// </summary>
/// <remarks>
/// <para>
/// Everything a creator typed is stored as they typed it. The normalized references beside that text —
/// cuisine, course, technique, yield unit — are additive and every one of them is nullable: a recipe with no
/// recognised anything is a complete, correct recipe, and recipes.md forbids a reference replacing entered
/// wording.
/// </para>
/// <para>
/// The aggregate boundary is this entity plus its six children. They have no lifetime of their own, no
/// repository of their own, and are always loaded and written through the recipe. Their composite foreign
/// keys carry <see cref="WorkspaceId"/> into the key itself, so a child that belongs to a different
/// workspace than its recipe is rejected by the database rather than only by
/// <see cref="CreatorPantry.Domain.Managers.Persistence.WorkspaceOwnershipInterceptor"/>.
/// </para>
/// <para>
/// No version, publication, AI, or nutrition fields live here. Versions arrive as their own immutable
/// snapshot entity; publication state belongs to provider-neutral <c>Publication</c> records; neither may
/// grow a column on this table (publishing.md).
/// </para>
/// </remarks>
public class Recipe : IWorkspaceOwned
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    /// <summary>The creator's working title, exactly as entered. Required; everything else here is optional.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>A short summary for listings and cards. Not the recipe's prose introduction.</summary>
    public string? Description { get; set; }

    /// <summary>
    /// The editorial introduction that runs above the recipe — the story, the why, the substitution the
    /// creator wants read before the ingredients. Named by recipes.md as creator-entered text to preserve.
    /// </summary>
    public string? Headnote { get; set; }

    /// <summary>The creator's own working notes on the recipe as a whole.</summary>
    public string? Notes { get; set; }

    /// <summary>
    /// How to keep it and for how long, in the creator's words. Descriptive, never a preservation or
    /// food-safety guarantee: recipes.md requires vetted references or an explicit caution for any claim of
    /// that kind, and free text here is neither.
    /// </summary>
    public string? StorageNotes { get; set; }

    /// <summary>"Adapted from…", in the creator's words. Free text because attribution rarely is not.</summary>
    public string? AttributionText { get; set; }

    /// <summary>Where the recipe came from, when there is a link to point at.</summary>
    public string? SourceUrl { get; set; }

    /// <summary>The shared <c>Cuisine</c> vocabulary, or null when the creator has not said.</summary>
    public Guid? CuisineId { get; set; }

    /// <summary>The shared <c>Course</c> vocabulary — the role this plays in a meal. One per recipe.</summary>
    public Guid? CourseId { get; set; }

    /// <summary>
    /// The shared <c>CookingTechnique</c> vocabulary: what the requirements call the recipe's method. One
    /// primary technique, not a set — a recipe that both braises and sears is described by its steps.
    /// </summary>
    public Guid? PrimaryTechniqueId { get; set; }

    public int? PrepTimeMinutes { get; set; }

    public int? CookTimeMinutes { get; set; }

    public int? RestTimeMinutes { get; set; }

    /// <summary>
    /// Stored rather than derived from the other three, and deliberately so. Prep overlaps cooking, resting
    /// is often unattended, and a creator who writes "about 2 hours, mostly waiting" means it. Summing the
    /// parts would overwrite a fact the creator stated with one the system inferred.
    /// </summary>
    public int? TotalTimeMinutes { get; set; }

    /// <summary>
    /// The yield exactly as the creator phrased it: "makes 12 muffins", "serves 4 to 6". This is the
    /// canonical yield. The structured pair below enriches it for scaling and never replaces it.
    /// </summary>
    public string? YieldText { get; set; }

    /// <summary>The numeric yield when one could be determined. Additive.</summary>
    public decimal? YieldQuantity { get; set; }

    /// <summary>
    /// The serving unit — <c>each</c>, <c>g</c>, <c>ml</c>. Null unless <see cref="YieldQuantity"/> is set.
    /// </summary>
    public Guid? YieldUnitId { get; set; }

    /// <summary>
    /// Mirrors the referenced unit's own dimension so the composite foreign key
    /// <c>(YieldUnitId, YieldUnitDimension)</c> can point at <c>MeasurementUnits (Id, Dimension)</c>, and
    /// <c>CK_Recipes_YieldUnit_Dimension</c> can rule out <see cref="MeasurementDimension.Temperature"/>.
    /// A yield measured in degrees is not a yield, and this makes that unrepresentable rather than merely
    /// unlikely. Redundant by design, exactly as <c>Ingredient.DefaultCountUnitDimension</c> is.
    /// </summary>
    public MeasurementDimension? YieldUnitDimension { get; set; }

    /// <summary>
    /// The creator's editorial state. Never a statement about external delivery — content.md keeps provider
    /// outcomes on publication records, and nothing may read <see cref="RecipeStatus.Ready"/> as published.
    /// </summary>
    public RecipeStatus Status { get; set; } = RecipeStatus.Draft;

    /// <summary>
    /// The <see cref="RecipeVersion"/> this recipe's content was copied from when it was created by
    /// duplicating another recipe; null for a recipe someone wrote.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>On the recipe, not on its version 1.</strong> Being a copy is a fact about this whole recipe
    /// and stays true however many times it is edited afterwards; the version row it would otherwise sit on
    /// describes one moment. <c>RecipeVersionSource.Duplicate</c> on version 1 says a duplication happened,
    /// and this says what it copied.
    /// </para>
    /// <para>
    /// <strong>One column, not two.</strong> The source recipe is <c>RecipeVersion.RecipeId</c> on the row
    /// this names, so storing it beside this would be a second copy of a fact that can then disagree with
    /// the first. A reader wanting the source recipe joins; a reader wanting the exact content that was
    /// copied already has it.
    /// </para>
    /// <para>
    /// <strong>Lineage, not dependence.</strong> The copy is canonical source material in its own right —
    /// editing either recipe does nothing to the other, and nothing here makes the copy a derivative of
    /// anything. The composite foreign key exists so that lineage cannot point outside the workspace; see
    /// <c>RecipeConfiguration</c> for what it costs.
    /// </para>
    /// </remarks>
    public Guid? DuplicatedFromVersionId { get; set; }

    /// <summary>
    /// The <c>WorkspaceMembership</c> of the creator who made this, not their Identity user id. Membership
    /// is the workspace-scoped identity (auth.md).
    /// </summary>
    /// <remarks>
    /// Not a foreign key, so that authorship survives a member leaving — which also means this is the one
    /// reference in the aggregate the database cannot keep inside the workspace. It must be taken from
    /// <see cref="CreatorPantry.Domain.Managers.Persistence.IWorkspaceContext.MembershipId"/> and never from
    /// a request field, because nothing below the write seam will catch it if it is not.
    /// </remarks>
    public Guid CreatedByMembershipId { get; set; }

    /// <inheritdoc cref="CreatedByMembershipId"/>
    public Guid UpdatedByMembershipId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// Optimistic concurrency token for collaborative editing. Two creators editing one recipe is the
    /// ordinary case, and a lost update here silently destroys written work; the second writer gets a
    /// recoverable conflict instead.
    /// </summary>
    public byte[] RowVersion { get; set; } = [];

    public ICollection<RecipeIngredientGroup> IngredientGroups { get; set; } = [];

    public ICollection<RecipeInstructionGroup> InstructionGroups { get; set; } = [];

    public ICollection<RecipeEquipment> Equipment { get; set; } = [];

    public ICollection<RecipeAssetLink> AssetLinks { get; set; } = [];

    /// <summary>
    /// The creator's own tags. A set rather than a list, and links into the workspace's
    /// <see cref="WorkspaceTag"/> vocabulary rather than free text, so a tag can be renamed in one place.
    /// </summary>
    public ICollection<RecipeTag> Tags { get; set; } = [];
}

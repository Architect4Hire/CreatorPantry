using CreatorPantry.Domain.Modules.Ingredients.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Vocabulary.Data.Entities;
using CreatorPantry.Domain.Modules.Measurement.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Auth.Data.Entities;
using CreatorPantry.Domain.Managers.Outbox;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Audit;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Managers.Persistence;

/// <summary>The single DbContext for the modular monolith.</summary>
/// <param name="workspaceContext">
/// Optional: unavailable outside a request/operation scope (migrations, unrelated hosts). Every
/// <see cref="CreatorPantry.Domain.Managers.Persistence.IWorkspaceOwned"/> entity is filtered by it via <see cref="WorkspaceOwnershipConvention"/>.
/// </param>
public class CreatorPantryDbContext(DbContextOptions<CreatorPantryDbContext> options, IWorkspaceContext? workspaceContext = null)
    : IdentityDbContext<ApplicationUser, IdentityRole, string>(options), IWorkspaceIdSource
{
    /// <summary>The Aspire connection name; matches the database resource in the AppHost.</summary>
    public const string ConnectionName = "creatorpantrydb";

    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();

    public DbSet<Workspace> Workspaces => Set<Workspace>();

    /// <remarks>
    /// Unfiltered before a workspace is resolved (see <see cref="WorkspaceMembership"/>'s remarks) — a
    /// query here before resolution must supply its own explicit <c>WorkspaceId</c>/<c>UserId</c>
    /// predicate, the way <see cref="CreatorPantry.Domain.Modules.Tenancy.Data.WorkspaceRepository"/> does, or it reads every workspace.
    /// </remarks>
    public DbSet<WorkspaceMembership> WorkspaceMemberships => Set<WorkspaceMembership>();

    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    /// <remarks>
    /// Platform reference data (tenancy.md): shared across every workspace, so it is deliberately not
    /// <see cref="CreatorPantry.Domain.Managers.Persistence.IWorkspaceOwned"/> and carries no query filter. Unlike a workspace-owned set, this
    /// one is readable before a workspace is resolved.
    /// </remarks>
    public DbSet<MeasurementUnit> MeasurementUnits => Set<MeasurementUnit>();

    /// <inheritdoc cref="MeasurementUnits"/>
    public DbSet<UnitAlias> UnitAliases => Set<UnitAlias>();

    /// <inheritdoc cref="MeasurementUnits"/>
    public DbSet<FoodCategory> FoodCategories => Set<FoodCategory>();

    /// <inheritdoc cref="MeasurementUnits"/>
    public DbSet<Ingredient> Ingredients => Set<Ingredient>();

    /// <inheritdoc cref="MeasurementUnits"/>
    public DbSet<IngredientAlias> IngredientAliases => Set<IngredientAlias>();

    /// <inheritdoc cref="MeasurementUnits"/>
    public DbSet<Cuisine> Cuisines => Set<Cuisine>();

    /// <inheritdoc cref="MeasurementUnits"/>
    public DbSet<CuisineAlias> CuisineAliases => Set<CuisineAlias>();

    /// <inheritdoc cref="MeasurementUnits"/>
    public DbSet<Course> Courses => Set<Course>();

    /// <inheritdoc cref="MeasurementUnits"/>
    public DbSet<CourseAlias> CourseAliases => Set<CourseAlias>();

    /// <inheritdoc cref="MeasurementUnits"/>
    /// <remarks>
    /// <see cref="CookingTechnique.RequiresSafetyCaution"/> reads in one direction only: true requires a
    /// caution, false means none is attached — never that the technique is safe (recipes.md).
    /// </remarks>
    public DbSet<CookingTechnique> CookingTechniques => Set<CookingTechnique>();

    /// <inheritdoc cref="MeasurementUnits"/>
    public DbSet<CookingTechniqueAlias> CookingTechniqueAliases => Set<CookingTechniqueAlias>();

    /// <inheritdoc cref="MeasurementUnits"/>
    public DbSet<EquipmentType> EquipmentTypes => Set<EquipmentType>();

    /// <inheritdoc cref="MeasurementUnits"/>
    public DbSet<EquipmentTypeAlias> EquipmentTypeAliases => Set<EquipmentTypeAlias>();

    /// <inheritdoc cref="MeasurementUnits"/>
    public DbSet<ReferenceSource> ReferenceSources => Set<ReferenceSource>();

    /// <inheritdoc cref="MeasurementUnits"/>
    public DbSet<IngredientDensityReference> IngredientDensityReferences => Set<IngredientDensityReference>();

    /// <inheritdoc cref="MeasurementUnits"/>
    public DbSet<DietaryProfile> DietaryProfiles => Set<DietaryProfile>();

    /// <inheritdoc cref="MeasurementUnits"/>
    public DbSet<Allergen> Allergens => Set<Allergen>();

    /// <inheritdoc cref="MeasurementUnits"/>
    /// <remarks>
    /// A trait that is not here means unknown. Querying this set and finding nothing never means the
    /// ingredient is compatible with a profile (recipes.md).
    /// </remarks>
    public DbSet<IngredientDietaryTrait> IngredientDietaryTraits => Set<IngredientDietaryTrait>();

    /// <inheritdoc cref="MeasurementUnits"/>
    /// <remarks>
    /// A trait that is not here means unknown. Querying this set and finding nothing never means the
    /// ingredient is free of an allergen, and no caller may treat it that way (recipes.md, ai.md).
    /// </remarks>
    public DbSet<IngredientAllergenTrait> IngredientAllergenTraits => Set<IngredientAllergenTrait>();

    /// <remarks>
    /// Workspace-owned creator intellectual property (tenancy.md), and the root of the recipe aggregate.
    /// Filtered by <see cref="WorkspaceOwnershipConvention"/> like every other
    /// <see cref="CreatorPantry.Domain.Managers.Persistence.IWorkspaceOwned"/> entity, which means querying it
    /// before a workspace is resolved throws rather than quietly returning nothing.
    /// </remarks>
    public DbSet<Recipe> Recipes => Set<Recipe>();

    /// <remarks>
    /// Interior to the <see cref="Recipe"/> aggregate. Exposed as a set because EF needs the entity type in
    /// the model, not because anything may write one on its own: the aggregate is loaded and saved through
    /// its root, and these rows reach the database only by way of a recipe.
    /// </remarks>
    public DbSet<RecipeIngredientGroup> RecipeIngredientGroups => Set<RecipeIngredientGroup>();

    /// <inheritdoc cref="RecipeIngredientGroups"/>
    public DbSet<RecipeIngredient> RecipeIngredients => Set<RecipeIngredient>();

    /// <inheritdoc cref="RecipeIngredientGroups"/>
    public DbSet<RecipeInstructionGroup> RecipeInstructionGroups => Set<RecipeInstructionGroup>();

    /// <inheritdoc cref="RecipeIngredientGroups"/>
    public DbSet<RecipeInstructionStep> RecipeInstructionSteps => Set<RecipeInstructionStep>();

    /// <inheritdoc cref="RecipeIngredientGroups"/>
    public DbSet<RecipeEquipment> RecipeEquipment => Set<RecipeEquipment>();

    /// <inheritdoc cref="RecipeIngredientGroups"/>
    public DbSet<RecipeAssetLink> RecipeAssetLinks => Set<RecipeAssetLink>();

    /// <remarks>
    /// A second aggregate root, not part of <see cref="Recipe"/>: versions outlive the edits that supersede
    /// them, so this set is queried in its own right. Write-once — <see cref="ImmutableRecordInterceptor"/>
    /// refuses every update and delete of a row here.
    /// </remarks>
    public DbSet<RecipeVersion> RecipeVersions => Set<RecipeVersion>();

    /// <inheritdoc cref="RecipeVersions"/>
    /// <remarks>
    /// Separated from <see cref="RecipeVersions"/> so that listing a recipe's history does not read its
    /// archive. Query this only when a snapshot is actually needed.
    /// </remarks>
    public DbSet<RecipeVersionSnapshot> RecipeVersionSnapshots => Set<RecipeVersionSnapshot>();

    /// <remarks>
    /// The creator's own tag vocabulary — workspace-owned and defined entirely by them, unlike the shared
    /// <see cref="Cuisines"/> and <see cref="Courses"/> catalogues. A third aggregate root in the recipes
    /// module: tags are listed, renamed and retired on their own.
    /// </remarks>
    public DbSet<WorkspaceTag> WorkspaceTags => Set<WorkspaceTag>();

    /// <inheritdoc cref="RecipeIngredientGroups"/>
    public DbSet<RecipeTag> RecipeTags => Set<RecipeTag>();

    Guid? IWorkspaceIdSource.CurrentWorkspaceIdOrNull => workspaceContext is { IsResolved: true } context ? context.WorkspaceId : null;

    /// <remarks>
    /// Added here rather than at each <c>AddDbContext</c> call site (API host, migration service, tests) so
    /// every instance enforces <see cref="WorkspaceOwnershipInterceptor"/> and
    /// <see cref="AuditLogImmutabilityInterceptor"/> on save with nothing to forget. Additive over whatever
    /// provider/options DI already configured.
    /// </remarks>
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);

        optionsBuilder.AddInterceptors(
            new WorkspaceOwnershipInterceptor(),
            new AuditLogImmutabilityInterceptor(),
            new ImmutableRecordInterceptor());
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ApplyConfigurationsFromAssembly(typeof(CreatorPantryDbContext).Assembly);

        WorkspaceOwnershipConvention.Apply(builder, this);
    }
}

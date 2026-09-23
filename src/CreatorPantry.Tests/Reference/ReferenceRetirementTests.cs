using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Modules.Measurement.Data;
using CreatorPantry.Domain.Modules.Measurement.Managers;
using CreatorPantry.Domain.Modules.Vocabulary.Data;
using CreatorPantry.Domain.Modules.Vocabulary.Managers;
using CreatorPantry.Domain.Modules.Ingredients.Data;
using CreatorPantry.Domain.Modules.Ingredients.Managers;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Outbox;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Auth.Data.Entities;
using CreatorPantry.Domain.Modules.Measurement.Data.Entities;
using CreatorPantry.Domain.Modules.Vocabulary.Data.Entities;
using CreatorPantry.Domain.Modules.Ingredients.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Data;
using CreatorPantry.Domain.Modules.Auth.Data;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Tests.Reference;

/// <summary>
/// Every reference list promises, in its controller documentation, that "retired entries are not listed".
/// These are the tests that make that promise falsifiable.
/// </summary>
/// <remarks>
/// It was not, before. Every seeded row is active, so deleting <c>.Where(x =&gt; x.IsActive)</c> from all six
/// repository call sites left the entire suite green — the filter was asserted by nothing at all, while
/// <c>Responses_do_not_carry_an_active_flag</c> read like coverage of it. Each test here retires a real row
/// and then looks for it.
/// </remarks>
public sealed class ReferenceRetirementTests : IAsyncLifetime
{
    private ReferenceCatalogFixture _catalog = null!;

    public async ValueTask InitializeAsync() => _catalog = await ReferenceCatalogFixture.StartAsync();

    public async ValueTask DisposeAsync() => await _catalog.DisposeAsync();

    [Fact]
    public async Task A_retired_cuisine_leaves_the_list()
    {
        await RetireAsync(context => context.Cuisines, "italian");

        var (rows, _) = await Vocabularies().ListCuisinesAsync(Query(), Token);

        Assert.DoesNotContain(rows, row => row.Code == "italian");
        Assert.NotEmpty(rows);
    }

    /// <summary>A retired entry must not come back through its own alias either.</summary>
    [Fact]
    public async Task A_retired_cuisine_is_not_reachable_by_its_alias()
    {
        await RetireAsync(context => context.Cuisines, "american");

        var (rows, _) = await Vocabularies().ListCuisinesAsync(
            Query(ReferencePolicy.NormalizeSearch("USA")), Token);

        Assert.Empty(rows);
    }

    [Fact]
    public async Task A_retired_course_leaves_the_list()
    {
        await RetireAsync(context => context.Courses, "dessert");

        var (rows, _) = await Vocabularies().ListCoursesAsync(Query(), Token);

        Assert.DoesNotContain(rows, row => row.Code == "dessert");
    }

    [Fact]
    public async Task A_retired_technique_leaves_the_list()
    {
        await RetireAsync(context => context.CookingTechniques, "water-bath-canning");

        var (rows, _) = await Vocabularies().ListTechniquesAsync(Query(), Token);

        Assert.DoesNotContain(rows, row => row.Code == "water-bath-canning");
    }

    [Fact]
    public async Task A_retired_equipment_type_leaves_the_list()
    {
        await RetireAsync(context => context.EquipmentTypes, "wok");

        var (rows, _) = await Vocabularies().ListEquipmentTypesAsync(Query(), Token);

        Assert.DoesNotContain(rows, row => row.Code == "wok");
    }

    [Fact]
    public async Task A_retired_food_category_leaves_the_list()
    {
        await RetireAsync(context => context.FoodCategories, "baking");

        var (rows, _) = await Catalog().ListFoodCategoriesAsync(Query(), Token);

        Assert.DoesNotContain(rows, row => row.Code == "baking");
    }

    [Fact]
    public async Task A_retired_dietary_profile_leaves_the_list()
    {
        await RetireAsync(context => context.DietaryProfiles, "vegan");

        var (rows, _) = await Catalog().ListDietaryProfilesAsync(Query(), Token);

        Assert.DoesNotContain(rows, row => row.Code == "vegan");
    }

    [Fact]
    public async Task A_retired_allergen_leaves_the_list()
    {
        await RetireAsync(context => context.Allergens, "sesame");

        var (rows, _) = await Catalog().ListAllergensAsync(Query(), Token);

        Assert.DoesNotContain(rows, row => row.Code == "sesame");
    }

    [Fact]
    public async Task A_retired_unit_leaves_the_list()
    {
        await RetireAsync(context => context.MeasurementUnits, "dl");

        var (rows, _) = await Units().ListAsync(
            new MeasurementUnitQuery(null, null, null, Limit: 100, Scope: "t"), Token);

        Assert.DoesNotContain(rows, row => row.Code == "dl");
        Assert.NotEmpty(rows);
    }

    [Fact]
    public async Task A_retired_ingredient_leaves_the_list()
    {
        await using (var context = _catalog.CreateContext())
        {
            var honey = await context.Ingredients.SingleAsync(row => row.NormalizedName == "honey", Token);
            honey.IsActive = false;
            await context.SaveChangesAsync(Token);
        }

        var (rows, _) = await Ingredients().ListAsync(
            new IngredientQuery(null, null, null, Limit: 100, Scope: "t"), Token);

        Assert.DoesNotContain(rows, row => row.NormalizedName == "honey");
        Assert.NotEmpty(rows);
    }

    /// <summary>
    /// The seeder never deletes, so a row dropped from the catalogue definitions stays in the database. This
    /// pins the consequence: retiring it is what removes it from the product, and the two act together.
    /// </summary>
    [Fact]
    public async Task Retirement_is_what_removes_a_row_from_the_product_not_deletion()
    {
        await RetireAsync(context => context.Cuisines, "thai");

        await using var context = _catalog.CreateContext();

        Assert.True(await context.Cuisines.AnyAsync(row => row.Code == "thai", Token));
        Assert.False(await context.Cuisines.AnyAsync(row => row.Code == "thai" && row.IsActive, Token));
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static ReferenceQuery Query(ReferenceSearch? search = null) =>
        new(search, null, Limit: 100, Scope: "t");

    private async Task RetireAsync<TEntity>(
        Func<CreatorPantryDbContext, DbSet<TEntity>> set,
        string code)
        where TEntity : class
    {
        await using var context = _catalog.CreateContext();
        var entry = await set(context).SingleAsync(
            row => EF.Property<string>(row, "Code") == code, Token);

        context.Entry(entry).Property("IsActive").CurrentValue = false;
        await context.SaveChangesAsync(Token);
    }

    private IIngredientRepository Ingredients() => new IngredientRepository(_catalog.CreateContext());

    private IMeasurementUnitRepository Units() => new MeasurementUnitRepository(_catalog.CreateContext());

    private IControlledVocabularyRepository Vocabularies() => new ControlledVocabularyRepository(_catalog.CreateContext());

    private IReferenceCatalogRepository Catalog() => new ReferenceCatalogRepository(_catalog.CreateContext());
}

using CreatorPantry.Domain.Managers.Caching;
using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Ingredients;
using CreatorPantry.Domain.Modules.Ingredients.Facade;
using CreatorPantry.Domain.Modules.Ingredients.Managers;
using CreatorPantry.Domain.Modules.Measurement;
using CreatorPantry.Domain.Modules.Measurement.Facade;
using CreatorPantry.Domain.Modules.Measurement.Managers;
using CreatorPantry.Domain.Modules.Vocabulary;
using CreatorPantry.Domain.Modules.Vocabulary.Facade;
using CreatorPantry.Domain.Modules.Vocabulary.Managers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Reference;

/// <summary>
/// The contract the three reference modules hold with each other and with the shared kernel: distinct cache
/// identities, stable key strings, and a composition root each that stands on its own.
/// </summary>
/// <remarks>
/// Written after the Phase 4A audit found that splitting one reference facade into three modules left the
/// things keeping them apart — nine hard-coded resource literals in three files that no longer see each other
/// — asserted by nothing.
/// </remarks>
public sealed class ReferenceModuleContractTests
{
    private readonly FakeApplicationCache _cache = new();
    private readonly CountingReferenceBusiness _business = new();

    /// <summary>
    /// Every one of the nine routes caches under its own key.
    /// </summary>
    /// <remarks>
    /// The resource literal is the <em>only</em> thing separating one module's entries from another's, and it
    /// now lives in three files. A duplicate collides the cache key and the cursor scope at once: because the
    /// cached page is JSON, one catalogue's rows deserialize into another's shape and are served for the whole
    /// ten-minute lifetime. The previous test compared two Vocabulary resources and could not see this.
    /// </remarks>
    [Fact]
    public async Task All_nine_reference_resources_cache_under_distinct_keys()
    {
        var token = TestContext.Current.CancellationToken;
        var vocabulary = Vocabulary();
        var query = new ReferenceQueryViewModel();

        await Measurement().ListUnitsAsync(new MeasurementUnitQueryViewModel(), token);
        await Ingredients().ListIngredientsAsync(new IngredientQueryViewModel(), token);
        await vocabulary.ListFoodCategoriesAsync(query, token);
        await vocabulary.ListCuisinesAsync(query, token);
        await vocabulary.ListCoursesAsync(query, token);
        await vocabulary.ListTechniquesAsync(query, token);
        await vocabulary.ListEquipmentTypesAsync(query, token);
        await vocabulary.ListDietaryProfilesAsync(query, token);
        await vocabulary.ListAllergensAsync(query, token);

        Assert.Equal(9, _cache.Writes.Count);
        Assert.Equal(9, _cache.Writes.Distinct().Count());
        Assert.Equal(9, _business.Calls);
    }

    /// <summary>
    /// The exact key strings, pinned.
    /// </summary>
    /// <remarks>
    /// 4A.5 required that global cache key strings keep their exact values across the restructure. That claim
    /// was written into four code comments and verified by nothing — and because Phase 4 and 4A landed in one
    /// commit, there is no earlier artifact to diff against. Pinning the strings here is what makes the
    /// constraint real for the next restructure: a typo in any of the nine literals, or a change to the key
    /// layout, fails loudly instead of silently orphaning a cache partition.
    /// </remarks>
    [Theory]
    [InlineData("units", "global:reference:units:v2:limit=25|cursor=|dimension=|q=")]
    [InlineData("ingredients", "global:reference:ingredients:v2:limit=25|cursor=|category=|q=")]
    [InlineData("food-categories", "global:reference:food-categories:v2:limit=25|cursor=|q=")]
    [InlineData("cuisines", "global:reference:cuisines:v2:limit=25|cursor=|q=")]
    [InlineData("courses", "global:reference:courses:v2:limit=25|cursor=|q=")]
    [InlineData("techniques", "global:reference:techniques:v2:limit=25|cursor=|q=")]
    [InlineData("equipment-types", "global:reference:equipment-types:v2:limit=25|cursor=|q=")]
    [InlineData("dietary-profiles", "global:reference:dietary-profiles:v2:limit=25|cursor=|q=")]
    [InlineData("allergens", "global:reference:allergens:v2:limit=25|cursor=|q=")]
    public async Task The_default_page_of_each_resource_has_a_known_cache_key(string resource, string expected)
    {
        await ReadAsync(resource);

        Assert.Equal(expected, Assert.Single(_cache.Writes));
    }

    /// <summary>
    /// A cursor issued by one module is refused by another.
    /// </summary>
    /// <remarks>
    /// The existing cross-resource cursor tests are cuisines-to-allergens — both inside Vocabulary, so they
    /// never cross a module boundary. These do.
    /// </remarks>
    [Theory]
    [InlineData("units", "ingredients")]
    [InlineData("units", "cuisines")]
    [InlineData("ingredients", "units")]
    [InlineData("cuisines", "units")]
    public async Task A_cursor_from_one_module_is_refused_by_another(string issuer, string replayer)
    {
        var cursor = ReferenceCursor.Encode(
            "x", "y", ReferenceQueryKey.Scope(issuer, null, FiltersFor(issuer)));

        var result = await ReadAsync(replayer, cursor);

        Assert.False(result.Succeeded);
        Assert.Equal(ReferenceErrorCodes.CursorInvalid, result.Error!.Code);
        Assert.Equal(0, _business.Calls);
    }

    /// <summary>
    /// A malformed cursor is a cursor error, not a generic query error, on every route.
    /// </summary>
    /// <remarks>
    /// <c>CachedPageReader</c> tells the two apart by matching the FluentValidation property name against the
    /// literal <c>"Cursor"</c>. That link is conventional rather than compile-checked, and it was exercised
    /// through Vocabulary's view model only — so a rename of <c>MeasurementUnitQueryViewModel.Cursor</c> would
    /// have silently downgraded the stable error code on <c>/units</c> with a green suite.
    /// </remarks>
    [Theory]
    [InlineData("units")]
    [InlineData("ingredients")]
    [InlineData("food-categories")]
    [InlineData("cuisines")]
    [InlineData("courses")]
    [InlineData("techniques")]
    [InlineData("equipment-types")]
    [InlineData("dietary-profiles")]
    [InlineData("allergens")]
    public async Task A_malformed_cursor_is_reported_as_a_cursor_error_on_every_route(string resource)
    {
        var result = await ReadAsync(resource, "not-a-cursor!!");

        Assert.False(result.Succeeded);
        Assert.Equal(ReferenceErrorCodes.CursorInvalid, result.Error!.Code);
        Assert.True(result.Error.FieldErrors.ContainsKey("cursor"), "the field-error key must stay 'cursor'");
    }

    // --- Composition roots ---------------------------------------------------------------------------------

    /// <summary>
    /// Each module's composition root stands on its own.
    /// </summary>
    /// <remarks>
    /// <c>AddPaging</c> uses <c>TryAdd</c> and all three modules call it, so with all three registered together
    /// — which is what <c>Program.cs</c> does and therefore what every endpoint test exercises — deleting the
    /// call from any one of them changes nothing. A host that registers only one module would then fail to
    /// resolve <c>CachedPageReader</c> at the first request. Registering exactly one module here is the only
    /// way to see that.
    /// </remarks>
    [Theory]
    [InlineData("measurement")]
    [InlineData("vocabulary")]
    [InlineData("ingredients")]
    public void A_module_resolves_its_whole_seam_when_registered_alone(string module)
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<CreatorPantryDbContext>(options => options.UseSqlite(connection));
        services.AddSingleton<IApplicationCache, FakeApplicationCache>();

        switch (module)
        {
            case "measurement": services.AddMeasurementModule(); break;
            case "vocabulary": services.AddVocabularyModule(); break;
            default: services.AddIngredientModule(); break;
        }

        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();

        // Resolving the facade pulls the whole chain: validator, business, data layer, repository, page reader.
        object facade = module switch
        {
            "measurement" => scope.ServiceProvider.GetRequiredService<IMeasurementFacade>(),
            "vocabulary" => scope.ServiceProvider.GetRequiredService<IVocabularyFacade>(),
            _ => scope.ServiceProvider.GetRequiredService<IIngredientFacade>(),
        };

        Assert.NotNull(facade);
    }

    // --- Harness -------------------------------------------------------------------------------------------

    private static (string Name, string? Value)[] FiltersFor(string resource) => resource switch
    {
        "units" => [("dimension", null)],
        "ingredients" => [("category", null)],
        _ => [],
    };

    private async Task<Domain.Managers.Results.OperationResult<bool>> ReadAsync(string resource, string? cursor = null)
    {
        var token = TestContext.Current.CancellationToken;
        var vocabulary = Vocabulary();
        var query = new ReferenceQueryViewModel(Cursor: cursor);

        return resource switch
        {
            "units" => Flatten(await Measurement().ListUnitsAsync(new MeasurementUnitQueryViewModel(Cursor: cursor), token)),
            "ingredients" => Flatten(await Ingredients().ListIngredientsAsync(new IngredientQueryViewModel(Cursor: cursor), token)),
            "food-categories" => Flatten(await vocabulary.ListFoodCategoriesAsync(query, token)),
            "cuisines" => Flatten(await vocabulary.ListCuisinesAsync(query, token)),
            "courses" => Flatten(await vocabulary.ListCoursesAsync(query, token)),
            "techniques" => Flatten(await vocabulary.ListTechniquesAsync(query, token)),
            "equipment-types" => Flatten(await vocabulary.ListEquipmentTypesAsync(query, token)),
            "dietary-profiles" => Flatten(await vocabulary.ListDietaryProfilesAsync(query, token)),
            _ => Flatten(await vocabulary.ListAllergensAsync(query, token)),
        };
    }

    /// <summary>Collapses the nine differently-typed results to the success/error pair these tests assert on.</summary>
    private static Domain.Managers.Results.OperationResult<bool> Flatten<T>(
        Domain.Managers.Results.OperationResult<CursorPageServiceModel<T>> result) =>
        result.Succeeded
            ? Domain.Managers.Results.OperationResult<bool>.Success(true)
            : Domain.Managers.Results.OperationResult<bool>.Failure(result.Error!);

    private IVocabularyFacade Vocabulary() =>
        new VocabularyFacade(new ReferenceQueryViewModelValidator(), _business, new CachedPageReader(_cache));

    private IMeasurementFacade Measurement() =>
        new MeasurementFacade(new MeasurementUnitQueryViewModelValidator(), _business, _cache, new CachedPageReader(_cache));

    private IIngredientFacade Ingredients() =>
        new IngredientFacade(new IngredientQueryViewModelValidator(), _business, _cache, new CachedPageReader(_cache));
}

using CreatorPantry.Domain.Modules.Measurement.Business;
using CreatorPantry.Domain.Modules.Vocabulary.Business;
using CreatorPantry.Domain.Modules.Ingredients.Business;
using CreatorPantry.Domain.Managers.Caching;
using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Modules.Ingredients.Facade;
using CreatorPantry.Domain.Modules.Ingredients.Managers;
using CreatorPantry.Domain.Modules.Measurement.Facade;
using CreatorPantry.Domain.Modules.Measurement.Managers;
using CreatorPantry.Domain.Modules.Vocabulary.Facade;
using CreatorPantry.Domain.Modules.Vocabulary.Managers;

namespace CreatorPantry.Tests.Reference;

/// <summary>An in-process <see cref="IApplicationCache"/> that records every key it is asked about.</summary>
internal sealed class FakeApplicationCache : IApplicationCache
{
    private readonly Dictionary<string, object> _entries = [];

    public List<string> Reads { get; } = [];

    public List<string> Writes { get; } = [];

    /// <summary>Set to make every read miss, for testing the read-through path without clearing entries.</summary>
    public bool Disabled { get; set; }

    public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken)
        where T : class
    {
        Reads.Add(key);

        if (Disabled || !_entries.TryGetValue(key, out var value))
        {
            return Task.FromResult<T?>(null);
        }

        return Task.FromResult((T?)value);
    }

    public Task SetAsync<T>(string key, T value, TimeSpan lifetime, CancellationToken cancellationToken)
        where T : class
    {
        Writes.Add(key);
        _entries[key] = value;

        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key, CancellationToken cancellationToken)
    {
        _entries.Remove(key);

        return Task.CompletedTask;
    }
}

/// <summary>
/// Counts how often a facade reached past the cache. Returns canned pages: what these tests assert is whether
/// business was consulted, not what it would have said.
/// </summary>
/// <remarks>
/// One class implements all three reference modules' business interfaces so a single <see cref="Calls"/>
/// counter still aggregates across them. That is a test convenience, not a pattern to copy — in the
/// application each module has its own business type and nothing implements another module's.
/// </remarks>
internal sealed class CountingReferenceBusiness : IMeasurementBusiness, IVocabularyBusiness, IIngredientBusiness
{
    public int Calls { get; private set; }

    public Task<CursorPageServiceModel<IngredientServiceModel>> ListIngredientsAsync(
        IngredientQuery query, CancellationToken cancellationToken) =>
        Count(new CursorPageServiceModel<IngredientServiceModel>(
            [new IngredientServiceModel(Guid.Empty, "flour", "baking", null, ["plain flour"])], null));

    public Task<CursorPageServiceModel<MeasurementUnitServiceModel>> ListUnitsAsync(
        MeasurementUnitQuery query, CancellationToken cancellationToken) =>
        Count(new CursorPageServiceModel<MeasurementUnitServiceModel>([], null));

    public Task<CursorPageServiceModel<ReferenceEntryServiceModel>> ListFoodCategoriesAsync(
        ReferenceQuery query, CancellationToken cancellationToken) => Entries();

    public Task<CursorPageServiceModel<ReferenceEntryServiceModel>> ListCuisinesAsync(
        ReferenceQuery query, CancellationToken cancellationToken) => Entries();

    public Task<CursorPageServiceModel<ReferenceEntryServiceModel>> ListCoursesAsync(
        ReferenceQuery query, CancellationToken cancellationToken) => Entries();

    public Task<CursorPageServiceModel<CookingTechniqueServiceModel>> ListTechniquesAsync(
        ReferenceQuery query, CancellationToken cancellationToken) =>
        Count(new CursorPageServiceModel<CookingTechniqueServiceModel>([], null));

    public Task<CursorPageServiceModel<ReferenceEntryServiceModel>> ListEquipmentTypesAsync(
        ReferenceQuery query, CancellationToken cancellationToken) => Entries();

    public Task<CursorPageServiceModel<DescribedReferenceEntryServiceModel>> ListDietaryProfilesAsync(
        ReferenceQuery query, CancellationToken cancellationToken) =>
        Count(new CursorPageServiceModel<DescribedReferenceEntryServiceModel>([], null));

    public Task<CursorPageServiceModel<DescribedReferenceEntryServiceModel>> ListAllergensAsync(
        ReferenceQuery query, CancellationToken cancellationToken) =>
        Count(new CursorPageServiceModel<DescribedReferenceEntryServiceModel>([], null));

    private Task<CursorPageServiceModel<ReferenceEntryServiceModel>> Entries() =>
        Count(new CursorPageServiceModel<ReferenceEntryServiceModel>(
            [new ReferenceEntryServiceModel(Guid.Empty, "italian", "Italian")], null));

    private Task<T> Count<T>(T page)
    {
        Calls++;

        return Task.FromResult(page);
    }
}

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

    // Reference verification, used by the recipe facade rather than by the cached list endpoints these fakes
    // were written for. Deliberately not counted: Calls exists to prove the page cache stops repeat reads,
    // and a lookup that never goes through that cache would make the count mean two different things.
    public Task<bool> IsUsableAsync(
        CreatorPantry.Domain.Modules.Vocabulary.Facade.VocabularyCatalog catalog, Guid id, CancellationToken cancellationToken) =>
        Task.FromResult(true);

    // The Ingredients module's own version of the same check, used by the recipe facade for a submitted
    // ingredient reference. Uncounted for the same reason the vocabulary one is.
    public Task<bool> IsUsableAsync(Guid ingredientId, CancellationToken cancellationToken) =>
        Task.FromResult(true);

    public Task<CreatorPantry.Domain.Managers.Reference.MeasurementDimension?> FindUsableUnitDimensionAsync(
        Guid unitId, CancellationToken cancellationToken) =>
        Task.FromResult<CreatorPantry.Domain.Managers.Reference.MeasurementDimension?>(
            CreatorPantry.Domain.Managers.Reference.MeasurementDimension.Count);

    public Task<CursorPageServiceModel<IngredientServiceModel>> ListIngredientsAsync(
        IngredientQuery query, CancellationToken cancellationToken) =>
        Count(new CursorPageServiceModel<IngredientServiceModel>(
            [new IngredientServiceModel(Guid.Empty, "flour", "baking", null, ["plain flour"])], null));

    public Task<CursorPageServiceModel<MeasurementUnitServiceModel>> ListUnitsAsync(
        MeasurementUnitQuery query, CancellationToken cancellationToken) =>
        Count(new CursorPageServiceModel<MeasurementUnitServiceModel>([], null));

    // Not counted, for the same reason FindUsableUnitDimensionAsync is not: these fakes were written for the
    // cached list endpoints, and a batch unit lookup used elsewhere would make Calls mean two different things.
    public Task<IReadOnlyList<MeasurementUnitServiceModel>> FindUnitsByIdsAsync(
        IReadOnlyCollection<Guid> unitIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<MeasurementUnitServiceModel>>([]);

    public Task<IReadOnlyList<IngredientMatchIndexEntry>> LoadMatchIndexAsync(CancellationToken cancellationToken) =>
        Count<IReadOnlyList<IngredientMatchIndexEntry>>([]);

    public IReadOnlyList<IngredientMatchResult> ResolveCandidates(
        IReadOnlyList<string> candidateTexts, IReadOnlyList<IngredientMatchIndexEntry> index) =>
        candidateTexts.Select(text => new IngredientMatchResult { InputText = text }).ToList();

    Task<IReadOnlyList<UnitMatchIndexEntry>> IMeasurementBusiness.LoadMatchIndexAsync(CancellationToken cancellationToken) =>
        Count<IReadOnlyList<UnitMatchIndexEntry>>([]);

    IReadOnlyList<UnitMatchResult> IMeasurementBusiness.ResolveCandidates(
        IReadOnlyList<string> candidateTexts, IReadOnlyList<UnitMatchIndexEntry> index) =>
        candidateTexts.Select(text => new UnitMatchResult { InputText = text }).ToList();

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

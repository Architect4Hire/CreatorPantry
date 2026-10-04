using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Modules.Vocabulary.Data.Entities;
using CreatorPantry.Domain.Modules.Vocabulary.Managers;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Vocabulary.Data;

/// <summary>
/// Reads the four controlled vocabularies that describe a recipe: cuisine, course, technique, and equipment
/// type. Global reference data — no workspace filter applies to any of them.
/// </summary>
/// <remarks>
/// Four named methods rather than one <c>List&lt;T&gt;</c>: a caller asks for cuisines, and cannot ask this
/// for "entities of type T". The shared filtering behind them is a private detail, in the same spirit as
/// <c>ControlledVocabularyConfiguration&lt;T&gt;</c>, which already maps these four tables from one place.
/// </remarks>
public interface IControlledVocabularyRepository
{
    /// <summary>Whether the id names an active row in the given catalogue.</summary>
    Task<bool> IsUsableAsync(CreatorPantry.Domain.Modules.Vocabulary.Facade.VocabularyCatalog catalog, Guid id, CancellationToken cancellationToken);

    /// <summary>The display name of one row, retired or not, or <c>null</c> for an id in no such row.</summary>
    Task<string?> GetDisplayNameAsync(CreatorPantry.Domain.Modules.Vocabulary.Facade.VocabularyCatalog catalog, Guid id, CancellationToken cancellationToken);

    Task<(IReadOnlyList<ReferenceEntryRecord> Rows, bool HasMore)> ListCuisinesAsync(
        ReferenceQuery query, CancellationToken cancellationToken);

    Task<(IReadOnlyList<ReferenceEntryRecord> Rows, bool HasMore)> ListCoursesAsync(
        ReferenceQuery query, CancellationToken cancellationToken);

    Task<(IReadOnlyList<ReferenceEntryRecord> Rows, bool HasMore)> ListEquipmentTypesAsync(
        ReferenceQuery query, CancellationToken cancellationToken);

    /// <summary>Techniques carry their own safety-caution flag, so they have their own record type.</summary>
    Task<(IReadOnlyList<CookingTechniqueRecord> Rows, bool HasMore)> ListTechniquesAsync(
        ReferenceQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// Every active cuisine, unpaged and unsearched, ordered by code.
    /// </summary>
    /// <remarks>
    /// For a caller that needs the whole catalogue as a set to choose from rather than a page to show — the
    /// content-seed generator is the first. Unpaged is safe because these tables are small and bounded by seed
    /// data, and ordered by code rather than display name because a caller picking from the set needs a stable
    /// order, not a readable one.
    /// </remarks>
    Task<IReadOnlyList<ReferenceEntryRecord>> ListActiveCuisinesAsync(CancellationToken cancellationToken);

    /// <inheritdoc cref="ListActiveCuisinesAsync"/>
    Task<IReadOnlyList<ReferenceEntryRecord>> ListActiveCoursesAsync(CancellationToken cancellationToken);

    /// <inheritdoc cref="ListActiveCuisinesAsync"/>
    Task<IReadOnlyList<CookingTechniqueRecord>> ListActiveTechniquesAsync(CancellationToken cancellationToken);
}

internal sealed class ControlledVocabularyRepository(CreatorPantryDbContext context) : IControlledVocabularyRepository
{
    public Task<bool> IsUsableAsync(
        CreatorPantry.Domain.Modules.Vocabulary.Facade.VocabularyCatalog catalog, Guid id, CancellationToken cancellationToken) =>
        catalog switch
        {
            CreatorPantry.Domain.Modules.Vocabulary.Facade.VocabularyCatalog.Cuisine =>
                context.Cuisines.AsNoTracking().AnyAsync(row => row.Id == id && row.IsActive, cancellationToken),
            CreatorPantry.Domain.Modules.Vocabulary.Facade.VocabularyCatalog.Course =>
                context.Courses.AsNoTracking().AnyAsync(row => row.Id == id && row.IsActive, cancellationToken),
            CreatorPantry.Domain.Modules.Vocabulary.Facade.VocabularyCatalog.CookingTechnique =>
                context.CookingTechniques.AsNoTracking().AnyAsync(row => row.Id == id && row.IsActive, cancellationToken),
            // Exhaustive on purpose: a new catalogue must be answered here rather than silently reported
            // unusable, which would reject every id in it with no clue why.
            _ => throw new ArgumentOutOfRangeException(nameof(catalog), catalog, "Unknown vocabulary catalogue."),
        };

    public Task<string?> GetDisplayNameAsync(
        CreatorPantry.Domain.Modules.Vocabulary.Facade.VocabularyCatalog catalog, Guid id, CancellationToken cancellationToken) =>
        catalog switch
        {
            CreatorPantry.Domain.Modules.Vocabulary.Facade.VocabularyCatalog.Cuisine =>
                context.Cuisines.AsNoTracking().Where(row => row.Id == id)
                    .Select(row => row.DisplayName).SingleOrDefaultAsync(cancellationToken),
            CreatorPantry.Domain.Modules.Vocabulary.Facade.VocabularyCatalog.Course =>
                context.Courses.AsNoTracking().Where(row => row.Id == id)
                    .Select(row => row.DisplayName).SingleOrDefaultAsync(cancellationToken),
            CreatorPantry.Domain.Modules.Vocabulary.Facade.VocabularyCatalog.CookingTechnique =>
                context.CookingTechniques.AsNoTracking().Where(row => row.Id == id)
                    .Select(row => row.DisplayName).SingleOrDefaultAsync(cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(catalog), catalog, "Unknown vocabulary catalogue."),
        };

    public async Task<(IReadOnlyList<ReferenceEntryRecord> Rows, bool HasMore)> ListCuisinesAsync(
        ReferenceQuery query, CancellationToken cancellationToken)
    {
        var fetched = await Ordered(Filtered(context.Cuisines, context.CuisineAliases, query), query)
            .Select(entry => new ReferenceEntryRecord(entry.Id, entry.Code, entry.DisplayName))
            .ToListAsync(cancellationToken);

        return fetched.ToPage(query.Limit);
    }

    public async Task<(IReadOnlyList<ReferenceEntryRecord> Rows, bool HasMore)> ListCoursesAsync(
        ReferenceQuery query, CancellationToken cancellationToken)
    {
        var fetched = await Ordered(Filtered(context.Courses, context.CourseAliases, query), query)
            .Select(entry => new ReferenceEntryRecord(entry.Id, entry.Code, entry.DisplayName))
            .ToListAsync(cancellationToken);

        return fetched.ToPage(query.Limit);
    }

    public async Task<(IReadOnlyList<ReferenceEntryRecord> Rows, bool HasMore)> ListEquipmentTypesAsync(
        ReferenceQuery query, CancellationToken cancellationToken)
    {
        var fetched = await Ordered(Filtered(context.EquipmentTypes, context.EquipmentTypeAliases, query), query)
            .Select(entry => new ReferenceEntryRecord(entry.Id, entry.Code, entry.DisplayName))
            .ToListAsync(cancellationToken);

        return fetched.ToPage(query.Limit);
    }

    public async Task<(IReadOnlyList<CookingTechniqueRecord> Rows, bool HasMore)> ListTechniquesAsync(
        ReferenceQuery query, CancellationToken cancellationToken)
    {
        var fetched = await Ordered(
                Filtered(context.CookingTechniques, context.CookingTechniqueAliases, query), query)
            .Select(technique => new CookingTechniqueRecord(
                technique.Id, technique.Code, technique.DisplayName, technique.RequiresSafetyCaution))
            .ToListAsync(cancellationToken);

        return fetched.ToPage(query.Limit);
    }

    public async Task<IReadOnlyList<ReferenceEntryRecord>> ListActiveCuisinesAsync(CancellationToken cancellationToken) =>
        await ActiveEntries(context.Cuisines).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<ReferenceEntryRecord>> ListActiveCoursesAsync(CancellationToken cancellationToken) =>
        await ActiveEntries(context.Courses).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<CookingTechniqueRecord>> ListActiveTechniquesAsync(CancellationToken cancellationToken) =>
        await context.CookingTechniques
            .AsNoTracking()
            .Where(technique => technique.IsActive)
            .OrderBy(technique => technique.Code)
            .Select(technique => new CookingTechniqueRecord(
                technique.Id, technique.Code, technique.DisplayName, technique.RequiresSafetyCaution))
            .ToListAsync(cancellationToken);

    private static IQueryable<ReferenceEntryRecord> ActiveEntries<TVocabulary>(DbSet<TVocabulary> source)
        where TVocabulary : ControlledVocabulary =>
        source.AsNoTracking()
            .Where(entry => entry.IsActive)
            .OrderBy(entry => entry.Code)
            .Select(entry => new ReferenceEntryRecord(entry.Id, entry.Code, entry.DisplayName));

    /// <summary>Active entries, matching the search, resumed after the cursor.</summary>
    private static IQueryable<TVocabulary> Filtered<TVocabulary, TAlias>(
        DbSet<TVocabulary> source,
        DbSet<TAlias> aliasSource,
        ReferenceQuery query)
        where TVocabulary : ControlledVocabulary
        where TAlias : VocabularyAlias
    {
        var entries = source.AsNoTracking().Where(entry => entry.IsActive);

        if (query.Search is { } search)
        {
            var aliasMatches = aliasSource
                .Where(alias => alias.NormalizedAlias.Contains(search.Normalized))
                .Select(alias => alias.VocabularyId);

            // Display name in its raw lowercased form, alias keys in their normalized form: each surface is
            // searched in the form it is stored in. ToLower on both sides rather than relying on collation,
            // which differs between SQL Server and SQLite.
            entries = entries.Where(entry =>
                entry.DisplayName.ToLower().Contains(search.Raw)
                || entry.Code.Contains(search.Raw)
                || aliasMatches.Contains(entry.Id));
        }

        if (query.Cursor is { } cursor)
        {
            entries = entries.Where(entry =>
                string.Compare(entry.DisplayName, cursor.SortValue) > 0
                || (entry.DisplayName == cursor.SortValue && string.Compare(entry.Code, cursor.TieBreaker) > 0));
        }

        return entries;
    }

    private static IQueryable<TVocabulary> Ordered<TVocabulary>(IQueryable<TVocabulary> entries, ReferenceQuery query)
        where TVocabulary : ControlledVocabulary =>
        entries
            .OrderBy(entry => entry.DisplayName)
            .ThenBy(entry => entry.Code)
            .Take(query.Limit + 1);
}

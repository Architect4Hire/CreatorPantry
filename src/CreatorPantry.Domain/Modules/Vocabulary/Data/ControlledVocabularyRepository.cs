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
    Task<(IReadOnlyList<ReferenceEntryRecord> Rows, bool HasMore)> ListCuisinesAsync(
        ReferenceQuery query, CancellationToken cancellationToken);

    Task<(IReadOnlyList<ReferenceEntryRecord> Rows, bool HasMore)> ListCoursesAsync(
        ReferenceQuery query, CancellationToken cancellationToken);

    Task<(IReadOnlyList<ReferenceEntryRecord> Rows, bool HasMore)> ListEquipmentTypesAsync(
        ReferenceQuery query, CancellationToken cancellationToken);

    /// <summary>Techniques carry their own safety-caution flag, so they have their own record type.</summary>
    Task<(IReadOnlyList<CookingTechniqueRecord> Rows, bool HasMore)> ListTechniquesAsync(
        ReferenceQuery query, CancellationToken cancellationToken);
}

internal sealed class ControlledVocabularyRepository(CreatorPantryDbContext context) : IControlledVocabularyRepository
{
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

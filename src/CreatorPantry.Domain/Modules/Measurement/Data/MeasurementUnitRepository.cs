using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Modules.Measurement.Managers;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Measurement.Data;

/// <summary>Reads the shared unit catalogue. Global reference data: no workspace filter applies.</summary>
public interface IMeasurementUnitRepository
{
    /// <summary>The dimension of an active unit, or null when the id names none.</summary>
    Task<CreatorPantry.Domain.Managers.Reference.MeasurementDimension?> FindUsableUnitDimensionAsync(
        Guid unitId, CancellationToken cancellationToken);

    /// <summary>One page of active units, ordered by display name.</summary>
    Task<(IReadOnlyList<MeasurementUnitRecord> Rows, bool HasMore)> ListAsync(
        MeasurementUnitQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// Every active unit's code, display name, plural name, and abbreviation, plus every alias of an active
    /// unit, flattened into one list for <see cref="UnitMatcher"/> to resolve candidates against in memory.
    /// </summary>
    Task<IReadOnlyList<UnitMatchIndexEntry>> ListMatchIndexAsync(CancellationToken cancellationToken);
}

internal sealed class MeasurementUnitRepository(CreatorPantryDbContext context) : IMeasurementUnitRepository
{
    public async Task<CreatorPantry.Domain.Managers.Reference.MeasurementDimension?> FindUsableUnitDimensionAsync(
        Guid unitId, CancellationToken cancellationToken)
    {
        // Projected to the one column the caller needs rather than materialising the unit: nothing else about
        // it is any of the calling module.s business.
        var dimensions = await context.MeasurementUnits.AsNoTracking()
            .Where(unit => unit.Id == unitId && unit.IsActive)
            .Select(unit => unit.Dimension)
            .ToListAsync(cancellationToken);

        return dimensions.Count == 0 ? null : dimensions[0];
    }

    public async Task<(IReadOnlyList<MeasurementUnitRecord> Rows, bool HasMore)> ListAsync(
        MeasurementUnitQuery query, CancellationToken cancellationToken)
    {
        var units = context.MeasurementUnits.AsNoTracking().Where(unit => unit.IsActive);

        if (query.Dimension is { } dimension)
        {
            units = units.Where(unit => unit.Dimension == dimension);
        }

        if (query.Search is { } search)
        {
            // Unit aliases use their own normalization, which strips separators outright so that "fl. oz."
            // collapses to "floz". Applying the ingredient rule here instead would search for "fl oz" and miss
            // every stored unit alias.
            var aliasKey = MeasurementPolicy.NormalizeAlias(search.Raw);

            var aliasMatches = context.UnitAliases
                .Where(alias => alias.NormalizedAlias.Contains(aliasKey))
                .Select(alias => alias.MeasurementUnitId);

            // ToLower on both sides rather than relying on collation: SQL Server's default is
            // case-insensitive and SQLite's is not, and a search must mean the same thing in both.
            units = units.Where(unit =>
                unit.DisplayName.ToLower().Contains(search.Raw)
                || unit.PluralName.ToLower().Contains(search.Raw)
                || unit.Code.Contains(search.Raw)
                || aliasMatches.Contains(unit.Id));
        }

        if (query.Cursor is { } cursor)
        {
            units = units.Where(unit =>
                string.Compare(unit.DisplayName, cursor.SortValue) > 0
                || (unit.DisplayName == cursor.SortValue && string.Compare(unit.Code, cursor.TieBreaker) > 0));
        }

        var fetched = await units
            .OrderBy(unit => unit.DisplayName)
            .ThenBy(unit => unit.Code)
            .Take(query.Limit + 1)
            .Select(unit => new MeasurementUnitRecord(
                unit.Id,
                unit.Code,
                unit.DisplayName,
                unit.PluralName,
                unit.Abbreviation,
                unit.Dimension,
                unit.System,
                unit.BaseUnitFactor,
                unit.DisplayPrecision))
            .ToListAsync(cancellationToken);

        return fetched.ToPage(query.Limit);
    }

    public async Task<IReadOnlyList<UnitMatchIndexEntry>> ListMatchIndexAsync(CancellationToken cancellationToken)
    {
        var active = await context.MeasurementUnits.AsNoTracking()
            .Where(unit => unit.IsActive)
            .Select(unit => new
            {
                unit.Id,
                unit.Code,
                unit.DisplayName,
                unit.PluralName,
                unit.Abbreviation,
            })
            .ToListAsync(cancellationToken);

        // Code/DisplayName/PluralName/Abbreviation are stored as written, not normalized — MeasurementPolicy
        // has no SQL translation, so normalizing happens here, in memory, over the small active set.
        var alternateForms = active.SelectMany(unit => new[]
        {
            new UnitMatchIndexEntry(unit.Id, unit.DisplayName, MeasurementPolicy.NormalizeAlias(unit.Code), UnitMatchKind.Code),
            new UnitMatchIndexEntry(unit.Id, unit.DisplayName, MeasurementPolicy.NormalizeAlias(unit.DisplayName), UnitMatchKind.DisplayName),
            new UnitMatchIndexEntry(unit.Id, unit.DisplayName, MeasurementPolicy.NormalizeAlias(unit.PluralName), UnitMatchKind.PluralName),
            new UnitMatchIndexEntry(unit.Id, unit.DisplayName, MeasurementPolicy.NormalizeAlias(unit.Abbreviation), UnitMatchKind.Abbreviation),
        }).Where(entry => entry.NormalizedText.Length > 0); // an unset alternate form must never become a wildcard match

        var activeIds = active.Select(unit => unit.Id).ToHashSet();
        var namesById = active.ToDictionary(unit => unit.Id, unit => unit.DisplayName);

        // A retired unit's alias is excluded the same way ListAsync excludes the unit itself: it stays
        // readable for what already resolved to it, but it does not newly match.
        var aliases = await context.UnitAliases.AsNoTracking()
            .Where(alias => activeIds.Contains(alias.MeasurementUnitId))
            .Select(alias => new { alias.MeasurementUnitId, alias.NormalizedAlias })
            .ToListAsync(cancellationToken);

        var aliasEntries = aliases.Select(alias => new UnitMatchIndexEntry(
            alias.MeasurementUnitId, namesById[alias.MeasurementUnitId], alias.NormalizedAlias, UnitMatchKind.Alias));

        return [.. alternateForms, .. aliasEntries];
    }
}

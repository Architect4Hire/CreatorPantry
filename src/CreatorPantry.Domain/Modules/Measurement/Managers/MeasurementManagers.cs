using System.ComponentModel;
using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Reference;
using Microsoft.AspNetCore.Mvc;

namespace CreatorPantry.Domain.Modules.Measurement.Managers;

/// <summary>
/// The unit list query. Bound from the query string.
/// </summary>
/// <remarks>
/// <para>
/// There is no workspace parameter, and there must not be. The unit catalogue is the shared platform zone
/// (tenancy.md): it has no <c>WorkspaceId</c> to filter on, and its route is not under
/// <c>/workspaces/{slug}</c>, so no workspace is resolved while it runs.
/// </para>
/// <para>
/// Names are given explicitly in lowercase, and filter descriptions use
/// <see cref="DescriptionAttribute"/>: the generated OpenAPI document otherwise takes the C# name and, for a
/// parameter with no description of its own, falls back to repeating its endpoint's summary.
/// </para>
/// </remarks>
public sealed record MeasurementUnitQueryViewModel(
    [property: FromQuery(Name = "search")]
    [property: Description("Free text matched against unit names, codes, and aliases. Terms shorter than two characters are ignored.")]
    string? Search = null,
    [property: FromQuery(Name = "dimension")]
    [property: Description("Restrict results to one dimension, by name: Mass, Volume, Count, Temperature, or Qualitative.")]
    string? Dimension = null,
    [property: FromQuery(Name = "cursor")] string? Cursor = null,
    [property: FromQuery(Name = "limit")] int? Limit = null);

/// <summary>A unit list query after translation: search normalized, cursor decoded and scope-checked, page size clamped.</summary>
/// <param name="Scope">
/// Identifies the ordered set these filters select. Cursors minted for this page are bound to it, so one
/// cannot be replayed against another resource or another filter.
/// </param>
public sealed record MeasurementUnitQuery(
    ReferenceSearch? Search,
    MeasurementDimension? Dimension,
    ReferenceCursor? Cursor,
    int Limit,
    string Scope)
{
    /// <summary>Identifies this exact page for caching.</summary>
    public string CacheKeySegment =>
        ReferenceQueryKey.Build(Search, Cursor, Limit, ("dimension", Dimension?.ToString()));
}

/// <summary>What the unit repository returns: a plain record, never an EF entity.</summary>
public sealed record MeasurementUnitRecord(
    Guid Id,
    string Code,
    string DisplayName,
    string PluralName,
    string Abbreviation,
    MeasurementDimension Dimension,
    MeasurementSystem System,
    decimal? BaseUnitFactor,
    int DisplayPrecision) : IReferenceRow
{
    public string SortValue => DisplayName;

    public string TieBreaker => Code;
}

/// <summary>A unit of measure, with everything a client needs to render and scale a quantity.</summary>
/// <param name="BaseUnitFactor">
/// How many of this dimension's base unit one of this unit equals. Null for temperature and qualitative
/// units, which have no multiplicative base — a client must not treat null as 1.
/// <para>
/// It is a scaling factor, not a conversion rate between units. Two units sharing a dimension are not
/// necessarily interconvertible: every <c>count</c> unit carries a factor of 1, meaning "one of these is one
/// item", which does <em>not</em> make one bunch equal to one clove. Conversion is a deterministic domain
/// operation gated by <see cref="MeasurementPolicy.MayConvert"/>, not arithmetic to reimplement in a browser.
/// </para>
/// </param>
/// <param name="DisplayPrecision">
/// Decimal places to render to. A display convention only — it says nothing about how accurately anything was
/// measured.
/// </param>
public sealed record MeasurementUnitServiceModel(
    Guid Id,
    string Code,
    string DisplayName,
    string PluralName,
    string Abbreviation,
    MeasurementDimension Dimension,
    MeasurementSystem System,
    decimal? BaseUnitFactor,
    int DisplayPrecision);

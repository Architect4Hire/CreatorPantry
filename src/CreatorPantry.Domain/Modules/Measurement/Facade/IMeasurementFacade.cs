using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Measurement.Data;
using CreatorPantry.Domain.Modules.Measurement.Managers;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Measurement.Facade;

public interface IMeasurementFacade
{
    /// <summary>
    /// The dimension of a unit still offered for new input, or <c>null</c> when the id names no such unit.
    /// </summary>
    /// <remarks>
    /// Exists so another module can both validate a unit reference and learn its dimension in one call — a
    /// recipe has to store the dimension alongside the unit id so the composite foreign key can pin the two
    /// together, and it cannot read this table itself. A retired or unknown unit answers null.
    /// </remarks>
    Task<CreatorPantry.Domain.Managers.Reference.MeasurementDimension?> FindUsableUnitDimensionAsync(
        Guid unitId, CancellationToken cancellationToken);

    Task<OperationResult<CursorPageServiceModel<MeasurementUnitServiceModel>>> ListUnitsAsync(
        MeasurementUnitQueryViewModel model, CancellationToken cancellationToken);

    /// <summary>
    /// The active units among the ids given, in no particular order, with their dimension and display
    /// precision together. Fewer than asked for is a normal answer — an id that names no active unit is simply
    /// absent, not an error.
    /// </summary>
    /// <remarks>
    /// Exists so a calculation seam that already knows which units it needs — unit conversion, most directly,
    /// which reads a source and a target unit's <c>BaseUnitFactor</c> together — can resolve all of them in one
    /// round trip rather than one call per id.
    /// </remarks>
    Task<IReadOnlyList<MeasurementUnitServiceModel>> FindUnitsByIdsAsync(
        IReadOnlyCollection<Guid> unitIds, CancellationToken cancellationToken);

    /// <summary>
    /// Matches each candidate string against the unit catalogue by exact/alias normalization, ranked by
    /// precedence with unresolved ambiguity called out rather than guessed (ING-001, AIREC-GR-003).
    /// </summary>
    /// <remarks>
    /// Read-only and global: no workspace context is required or accepted, matching every other read in this
    /// module. Results preserve the order of <paramref name="candidateTexts"/>.
    /// </remarks>
    Task<OperationResult<IReadOnlyList<UnitMatchResult>>> ResolveCandidatesAsync(
        IReadOnlyList<string> candidateTexts, CancellationToken cancellationToken);
}

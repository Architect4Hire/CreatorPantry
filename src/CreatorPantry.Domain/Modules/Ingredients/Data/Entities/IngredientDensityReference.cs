using CreatorPantry.Domain.Modules.Ingredients.Managers;
using CreatorPantry.Domain.Managers.Reference;

namespace CreatorPantry.Domain.Modules.Ingredients.Data.Entities;

/// <summary>
/// One cited mass-for-volume measurement of one ingredient under one stated condition — the only thing that
/// permits a mass-volume conversion. Global reference data: no <c>WorkspaceId</c>, not
/// <see cref="CreatorPantry.Domain.Managers.Persistence.IWorkspaceOwned"/>.
/// </summary>
/// <remarks>
/// <para>
/// There is no universal cup-to-gram figure and this model cannot express one: <see cref="IngredientId"/> is
/// required, so every density is about a specific ingredient, and <see cref="ReferenceSourceId"/> is required,
/// so every density says where it came from. A conversion with no matching approved row is reported as
/// unavailable, never guessed (recipes.md, B-08).
/// </para>
/// <para>
/// The condition is part of the measurement, not a footnote: sifted and scooped flour differ by roughly a
/// fifth, so the same ingredient legitimately holds several densities and the caller must choose one
/// deliberately.
/// </para>
/// <para>
/// Stored as the source's own mass and volume pair rather than a precomputed ratio, so a conversion divides
/// canonical figures instead of inheriting someone else's rounding.
/// </para>
/// </remarks>
public class IngredientDensityReference
{
    public Guid Id { get; set; }

    /// <summary>Required: a density detached from an ingredient would be exactly the universal figure that does not exist.</summary>
    public Guid IngredientId { get; set; }

    /// <summary>Required: provenance is not optional for a reference fact.</summary>
    public Guid ReferenceSourceId { get; set; }

    /// <summary>
    /// Always equal to the referenced source's <see cref="CreatorPantry.Domain.Modules.Ingredients.Data.Entities.ReferenceSource.Kind"/>.
    /// </summary>
    /// <remarks>
    /// Redundant by design. Carrying the kind here lets the composite foreign key
    /// <c>(ReferenceSourceId, ReferenceSourceKind)</c> point at <c>ReferenceSources (Id, Kind)</c>, which lets
    /// <c>CK_IngredientDensityReferences_AiEstimate_NotApproved</c> compare provenance against
    /// <see cref="ReviewStatus"/> in a single table. That is what makes "a model-estimated density can never be
    /// approved" a fact the database enforces, rather than a rule that holds until some import path forgets it.
    /// </remarks>
    public ReferenceSourceKind ReferenceSourceKind { get; set; }

    public decimal MassQuantity { get; set; }

    public Guid MassUnitId { get; set; }

    /// <summary>Always <see cref="MeasurementDimension.Mass"/>; see <see cref="Ingredient.DefaultCountUnitDimension"/> for the pattern.</summary>
    public MeasurementDimension MassUnitDimension { get; set; }

    public decimal VolumeQuantity { get; set; }

    public Guid VolumeUnitId { get; set; }

    /// <summary>Always <see cref="MeasurementDimension.Volume"/>.</summary>
    public MeasurementDimension VolumeUnitDimension { get; set; }

    /// <summary>
    /// The preparation or temperature the measurement holds for, as the source stated it — "sifted", "spooned
    /// and leveled", "packed", "at 20 °C". Empty when the source named no condition.
    /// </summary>
    public string ConditionNote { get; set; } = string.Empty;

    /// <summary>
    /// The lookup and uniqueness form of <see cref="ConditionNote"/>, from
    /// <see cref="DensityPolicy.NormalizeCondition"/>. Never null — see
    /// <see cref="DensityPolicy.UnspecifiedCondition"/> for why.
    /// </summary>
    public string NormalizedCondition { get; set; } = DensityPolicy.UnspecifiedCondition;

    /// <summary>
    /// Decimal places to round a value derived from this density to.
    /// </summary>
    /// <remarks>
    /// A presentation rule, and explicitly <em>not</em> a statement of measurement accuracy, uncertainty, or
    /// confidence. Rounding a number to three places does not make it right to three places, and nothing here
    /// should be read as a tolerance.
    /// </remarks>
    public int DisplayPrecision { get; set; }

    /// <summary>
    /// The date this figure applies from. A <see cref="DateOnly"/> rather than an instant: a citation is
    /// effective on a date, and giving it a time and an offset would invent precision the source never stated.
    /// </summary>
    /// <remarks>
    /// There is no matching end date. Supersession is a later row for the same ingredient, condition, and
    /// source, plus <see cref="DensityReviewStatus.Superseded"/> on the old one — a stored interval pair invites
    /// overlapping ranges that no cheap constraint can prevent.
    /// </remarks>
    public DateOnly EffectiveFrom { get; set; }

    public DensityReviewStatus ReviewStatus { get; set; }
}

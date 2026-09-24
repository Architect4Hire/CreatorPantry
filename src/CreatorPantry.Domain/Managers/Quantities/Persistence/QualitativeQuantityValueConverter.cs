using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace CreatorPantry.Domain.Managers.Quantities.Persistence;

/// <summary>
/// Maps <see cref="QualitativeQuantity"/> to a plain text column holding its descriptor — no JSON wrapper,
/// since the whole value is already a single string. Not wired to any entity by this prompt.
/// </summary>
public sealed class QualitativeQuantityValueConverter : ValueConverter<QualitativeQuantity, string>
{
    public QualitativeQuantityValueConverter()
        : base(
            quantity => quantity.Descriptor,
            descriptor => new QualitativeQuantity(descriptor))
    {
    }
}

/// <summary>Structural equality for <see cref="QualitativeQuantity"/> inside EF's change tracker.</summary>
public sealed class QualitativeQuantityValueComparer : ValueComparer<QualitativeQuantity>
{
    public QualitativeQuantityValueComparer()
        : base(
            (left, right) => left.Equals(right),
            quantity => quantity.GetHashCode())
    {
    }
}

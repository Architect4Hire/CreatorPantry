using System.Text.Json;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace CreatorPantry.Domain.Managers.Quantities.Persistence;

/// <summary>
/// Maps <see cref="Quantity"/> to a single JSON-text column (via <see cref="QuantityJsonConverter"/>), for a
/// module that wants one column instead of the flattened numerator/denominator columns
/// <c>RecipeIngredient</c> uses today. Not wired to any entity by this prompt.
/// </summary>
public sealed class QuantityValueConverter : ValueConverter<Quantity, string>
{
    public QuantityValueConverter()
        : base(
            quantity => JsonSerializer.Serialize(quantity, JsonSerializerOptions.Default),
            json => JsonSerializer.Deserialize<Quantity>(json, JsonSerializerOptions.Default))
    {
    }
}

/// <summary>Structural equality for <see cref="Quantity"/> inside EF's change tracker.</summary>
public sealed class QuantityValueComparer : ValueComparer<Quantity>
{
    public QuantityValueComparer()
        : base(
            (left, right) => left.Equals(right),
            quantity => quantity.GetHashCode())
    {
    }
}

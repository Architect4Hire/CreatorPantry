using System.Text.Json;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace CreatorPantry.Domain.Managers.Quantities.Persistence;

/// <summary>
/// Maps <see cref="QuantityRange"/> to a single JSON-text column (via <see cref="QuantityRangeJsonConverter"/>).
/// Not wired to any entity by this prompt.
/// </summary>
public sealed class QuantityRangeValueConverter : ValueConverter<QuantityRange, string>
{
    public QuantityRangeValueConverter()
        : base(
            range => JsonSerializer.Serialize(range, JsonSerializerOptions.Default),
            json => JsonSerializer.Deserialize<QuantityRange>(json, JsonSerializerOptions.Default))
    {
    }
}

/// <summary>Structural equality for <see cref="QuantityRange"/> inside EF's change tracker.</summary>
public sealed class QuantityRangeValueComparer : ValueComparer<QuantityRange>
{
    public QuantityRangeValueComparer()
        : base(
            (left, right) => left.Equals(right),
            range => range.GetHashCode())
    {
    }
}

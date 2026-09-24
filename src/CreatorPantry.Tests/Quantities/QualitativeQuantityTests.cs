using System.Text.Json;
using CreatorPantry.Domain.Managers.Quantities;
using CsCheck;

namespace CreatorPantry.Tests.Quantities;

public sealed class QualitativeQuantityTests
{
    private static readonly Gen<string> NonBlankDescriptor =
        Gen.String[1, 40].Where(s => !string.IsNullOrWhiteSpace(s));

    private static readonly Gen<string> BlankDescriptor =
        Gen.OneOfConst("", " ", "\t", "\n", "   \t  ");

    [Fact]
    public void Round_trips_through_the_JSON_contract_with_the_original_text_trimmed()
    {
        NonBlankDescriptor.Sample(descriptor =>
        {
            var quantity = new QualitativeQuantity(descriptor);
            var json = JsonSerializer.Serialize(quantity);
            var roundTripped = JsonSerializer.Deserialize<QualitativeQuantity>(json);

            Assert.Equal(quantity, roundTripped);
            Assert.Equal(descriptor.Trim(), roundTripped.Descriptor);
        });
    }

    [Fact]
    public void A_blank_descriptor_is_rejected()
    {
        BlankDescriptor.Sample(descriptor =>
            Assert.Throws<ArgumentException>(() => new QualitativeQuantity(descriptor)));
    }

    [Theory]
    [InlineData("to taste")]
    [InlineData("a pinch")]
    [InlineData("as needed")]
    [InlineData("a splash")]
    public void Common_qualitative_phrases_are_preserved_verbatim(string descriptor)
    {
        var quantity = new QualitativeQuantity(descriptor);

        Assert.Equal(descriptor, quantity.Descriptor);
        Assert.Equal(descriptor, quantity.ToString());
    }

    [Fact]
    public void Surrounding_whitespace_is_trimmed_but_interior_text_is_untouched()
    {
        var quantity = new QualitativeQuantity("  a generous pinch  ");

        Assert.Equal("a generous pinch", quantity.Descriptor);
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;
using CreatorPantry.Domain.Managers.Patching;

namespace CreatorPantry.Tests.Patching;

/// <summary>
/// The three states of a patch field, through the real serializer.
/// </summary>
/// <remarks>
/// Deserialization rather than unit-testing the struct, because the distinction between "absent" and "null"
/// is made by System.Text.Json and not by any code in <see cref="PatchField{T}"/>: it exists because a
/// converter is only invoked for a property that is present. A test that constructed the values directly
/// would prove the struct holds what it was handed and nothing about the thing that could actually break.
/// </remarks>
public sealed class PatchFieldTests
{
    /// <summary>Matches the API's own serializer: camelCase, and enums written as names.</summary>
    private static readonly JsonSerializerOptions Web = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());

        return options;
    }

    private enum Flavour
    {
        Sweet = 0,
        Savoury = 1,
    }

    private sealed record Body
    {
        public PatchField<string?> Text { get; init; }

        public PatchField<Guid?> Reference { get; init; }

        public PatchField<int?> Count { get; init; }

        public PatchField<decimal?> Quantity { get; init; }

        public PatchField<Flavour?> Flavour { get; init; }

        public PatchField<IReadOnlyList<string?>?> Names { get; init; }
    }

    private static Body Read(string json) => JsonSerializer.Deserialize<Body>(json, Web)!;

    // ---- Absent ----

    [Fact]
    public void An_empty_body_submits_nothing()
    {
        var body = Read("{}");

        Assert.False(body.Text.IsSubmitted);
        Assert.False(body.Reference.IsSubmitted);
        Assert.False(body.Count.IsSubmitted);
        Assert.False(body.Quantity.IsSubmitted);
        Assert.False(body.Flavour.IsSubmitted);
        Assert.False(body.Names.IsSubmitted);
    }

    [Fact]
    public void An_unmentioned_field_keeps_the_value_it_had()
    {
        var body = Read("""{"count":3}""");

        // The whole point: a client that does not know about `text` cannot blank it.
        Assert.Equal("unchanged", body.Text.Or("unchanged"));
        Assert.Equal(3, body.Count.Value);
    }

    [Fact]
    public void Reading_the_value_of_an_unsubmitted_field_throws()
    {
        var body = Read("{}");

        // Returning default would make "not mentioned" and "explicitly cleared" the same answer, which is
        // the confusion this type exists to prevent.
        Assert.Throws<InvalidOperationException>(() => body.Text.Value);
    }

    // ---- Submitted with a value ----

    [Theory]
    [InlineData("""{"text":"cake"}""")]
    [InlineData("""{"text":""}""")]
    public void A_present_string_is_submitted(string json)
    {
        var body = Read(json);

        Assert.True(body.Text.IsSubmitted);
        Assert.NotNull(body.Text.Value);
    }

    [Fact]
    public void Every_inner_type_round_trips()
    {
        var id = Guid.NewGuid();
        var body = Read($$"""
            {
              "text": "olive oil cake",
              "reference": "{{id}}",
              "count": 12,
              "quantity": 2.5,
              "flavour": "Savoury",
              "names": ["weeknight", "citrus"]
            }
            """);

        Assert.Equal("olive oil cake", body.Text.Value);
        Assert.Equal(id, body.Reference.Value);
        Assert.Equal(12, body.Count.Value);
        Assert.Equal(2.5m, body.Quantity.Value);
        Assert.Equal(Flavour.Savoury, body.Flavour.Value);
        Assert.Equal(["weeknight", "citrus"], body.Names.Value!);
    }

    [Fact]
    public void An_empty_array_is_submitted_and_empty()
    {
        var body = Read("""{"names":[]}""");

        // Distinct from absent, and the way a caller asks for every one of something to be removed.
        Assert.True(body.Names.IsSubmitted);
        Assert.Empty(body.Names.Value!);
    }

    // ---- Submitted as null ----

    [Fact]
    public void An_explicit_null_is_submitted_as_a_clear()
    {
        var body = Read("""
            {
              "text": null,
              "reference": null,
              "count": null,
              "quantity": null,
              "flavour": null,
              "names": null
            }
            """);

        Assert.True(body.Text.IsSubmitted);
        Assert.True(body.Reference.IsSubmitted);
        Assert.True(body.Count.IsSubmitted);
        Assert.True(body.Quantity.IsSubmitted);
        Assert.True(body.Flavour.IsSubmitted);
        Assert.True(body.Names.IsSubmitted);

        Assert.Null(body.Text.Value);
        Assert.Null(body.Reference.Value);
        Assert.Null(body.Count.Value);
        Assert.Null(body.Quantity.Value);
        Assert.Null(body.Flavour.Value);
        Assert.Null(body.Names.Value);
    }

    [Fact]
    public void A_cleared_field_overrides_the_current_value()
    {
        var body = Read("""{"text":null}""");

        // The case that separates this type from a nullable property: Or must answer null, not "current".
        Assert.Null(body.Text.Or("current"));
    }

    // ---- Writing ----

    [Fact]
    public void A_patch_field_refuses_to_be_written()
    {
        // There is no wire form for "absent", so any document this produced would ask for something other
        // than what the value says. Failing loudly beats a plausible-looking lie.
        var thrown = Assert.Throws<NotSupportedException>(
            () => JsonSerializer.Serialize(new Body { Text = PatchField<string?>.Submitted("cake") }, Web));

        Assert.Contains("absent", thrown.Message, StringComparison.Ordinal);
    }
}

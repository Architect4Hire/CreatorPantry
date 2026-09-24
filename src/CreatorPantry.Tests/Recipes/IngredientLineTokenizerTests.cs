using CreatorPantry.Domain.Managers.Quantities;
using CreatorPantry.Domain.Modules.Recipes.Managers;

namespace CreatorPantry.Tests.Recipes;

/// <summary>
/// A checked-in fixture set for <see cref="IngredientLineTokenizer"/>: fractions, mixed numbers, ranges,
/// package sizes, Unicode fractions, qualitative lines, group markers, invalid denominators, optionality,
/// preparation-clause splitting, and lines the tokenizer deliberately leaves ambiguous rather than guessing.
/// </summary>
/// <remarks>
/// The tokenizer touches no database and no vocabulary, so every case here is fully determined by the input
/// string — no fixture depends on seed data or configuration.
/// </remarks>
public sealed class IngredientLineTokenizerTests
{
    private static void AssertSingleQuantity(IngredientLineTokens tokens, string expectedSpan, long numerator, long denominator)
    {
        Assert.NotNull(tokens.Quantity);
        Assert.False(tokens.Quantity!.IsInvalid);
        Assert.Equal(expectedSpan, tokens.Quantity.Span.Text);
        Assert.Equal(Quantity.FromFraction(numerator, denominator), tokens.Quantity.Value);
        Assert.Null(tokens.Quantity.Range);
    }

    private static void AssertUnitAndIngredient(IngredientLineTokens tokens, string? expectedUnit, string expectedIngredient)
    {
        Assert.Equal(expectedUnit, tokens.UnitCandidate?.Text);
        Assert.Equal(expectedIngredient, tokens.IngredientText?.Text);
    }

    // ---- Integers and decimals ----

    [Theory]
    [InlineData("2 cups flour", "2", 2, 1, "cups", "flour")]
    [InlineData("2.5 cups sugar", "2.5", 5, 2, "cups", "sugar")]
    [InlineData("0.25 tsp salt", "0.25", 1, 4, "tsp", "salt")]
    public void Integers_and_decimals(string line, string span, long num, long den, string unit, string ingredient)
    {
        var tokens = IngredientLineTokenizer.Tokenize(line);

        AssertSingleQuantity(tokens, span, num, den);
        AssertUnitAndIngredient(tokens, unit, ingredient);
        Assert.Empty(tokens.Ambiguities);
    }

    // ---- Fractions and mixed numbers ----

    [Theory]
    [InlineData("1/2 cup milk", "1/2", 1, 2, "cup", "milk")]
    [InlineData("3/4 cup sugar", "3/4", 3, 4, "cup", "sugar")]
    [InlineData("1 1/2 cups flour", "1 1/2", 3, 2, "cups", "flour")]
    [InlineData("2 1/3 cups water", "2 1/3", 7, 3, "cups", "water")]
    public void Fractions_and_mixed_numbers(string line, string span, long num, long den, string unit, string ingredient)
    {
        var tokens = IngredientLineTokenizer.Tokenize(line);

        AssertSingleQuantity(tokens, span, num, den);
        AssertUnitAndIngredient(tokens, unit, ingredient);
        Assert.Empty(tokens.Ambiguities);
    }

    // ---- Unicode fractions ----

    [Theory]
    [InlineData("½ cup butter", "½", 1, 2, "cup", "butter")]
    [InlineData("¾ tsp vanilla", "¾", 3, 4, "tsp", "vanilla")]
    [InlineData("1½ cups sugar", "1½", 3, 2, "cups", "sugar")]
    [InlineData("2 ⅓ cups rice", "2 ⅓", 7, 3, "cups", "rice")]
    public void Unicode_fractions_bare_and_mixed_with_an_integer(string line, string span, long num, long den, string unit, string ingredient)
    {
        var tokens = IngredientLineTokenizer.Tokenize(line);

        AssertSingleQuantity(tokens, span, num, den);
        AssertUnitAndIngredient(tokens, unit, ingredient);
        Assert.Empty(tokens.Ambiguities);
    }

    // ---- Ranges ----

    [Theory]
    [InlineData("2-3 lb chicken", "2-3", "lb", "chicken")]
    [InlineData("2 - 3 lb chicken", "2 - 3", "lb", "chicken")]
    [InlineData("2 to 3 tablespoons olive oil", "2 to 3", "tablespoons", "olive oil")]
    [InlineData("2–3 cups broth", "2–3", "cups", "broth")] // en dash
    [InlineData("2—3 cups broth", "2—3", "cups", "broth")] // em dash
    public void Ranges(string line, string span, string unit, string ingredient)
    {
        var tokens = IngredientLineTokenizer.Tokenize(line);

        Assert.NotNull(tokens.Quantity);
        Assert.False(tokens.Quantity!.IsInvalid);
        Assert.Equal(span, tokens.Quantity.Span.Text);
        Assert.Equal(QuantityRange.Create(Quantity.FromInt(2), Quantity.FromInt(3)), tokens.Quantity.Range);
        AssertUnitAndIngredient(tokens, unit, ingredient);
        Assert.Empty(tokens.Ambiguities);
    }

    // ---- Package sizes ----

    [Fact]
    public void A_package_size_captures_the_count_and_the_nested_package_quantity_separately()
    {
        var tokens = IngredientLineTokenizer.Tokenize("1 (14.5 oz) can diced tomatoes");

        AssertSingleQuantity(tokens, "1", 1, 1);

        Assert.NotNull(tokens.PackageQuantity);
        Assert.Equal("(14.5 oz)", tokens.PackageQuantity!.Span.Text);
        Assert.False(tokens.PackageQuantity.Quantity.IsInvalid);
        Assert.Equal("14.5", tokens.PackageQuantity.Quantity.Span.Text);
        Assert.Equal(Quantity.FromFraction(29, 2), tokens.PackageQuantity.Quantity.Value);
        Assert.Equal("oz", tokens.PackageQuantity.Unit.Text);

        AssertUnitAndIngredient(tokens, "can", "diced tomatoes");
        Assert.Empty(tokens.Ambiguities);
    }

    [Fact]
    public void A_plural_package_unit_and_a_multi_word_ingredient()
    {
        var tokens = IngredientLineTokenizer.Tokenize("2 (15 oz) cans black beans");

        AssertSingleQuantity(tokens, "2", 2, 1);
        Assert.Equal(Quantity.FromInt(15), tokens.PackageQuantity!.Quantity.Value);
        Assert.Equal("oz", tokens.PackageQuantity.Unit.Text);
        AssertUnitAndIngredient(tokens, "cans", "black beans");
    }

    // ---- Qualitative lines (no numeric quantity) ----

    [Theory]
    [InlineData("salt to taste", "salt to taste", null)]
    [InlineData("Kosher salt, for finishing", "Kosher salt", "for finishing")]
    public void Qualitative_lines_have_no_quantity_and_are_flagged_rather_than_guessed(string line, string ingredient, string? preparation)
    {
        var tokens = IngredientLineTokenizer.Tokenize(line);

        Assert.Null(tokens.Quantity);
        Assert.Null(tokens.UnitCandidate);
        Assert.Equal(ingredient, tokens.IngredientText?.Text);
        Assert.Equal(preparation is null ? [] : new[] { preparation }, tokens.PreparationText.Select(s => s.Text));

        var ambiguity = Assert.Single(tokens.Ambiguities);
        Assert.Equal(IngredientLineAmbiguityKind.NoQuantityDetected, ambiguity.Kind);
        Assert.Equal(line, ambiguity.Span.Text);
    }

    // ---- Group markers ----

    [Theory]
    [InlineData("For the crust:")]
    [InlineData("Crust:")]
    [InlineData("For the streusel topping:")]
    public void Group_markers_are_not_ingredient_lines(string line)
    {
        var tokens = IngredientLineTokenizer.Tokenize(line);

        Assert.True(tokens.IsGroupMarker);
        Assert.Null(tokens.Quantity);
        Assert.Null(tokens.IngredientText);
        Assert.Empty(tokens.Ambiguities);
    }

    // ---- Invalid denominators (and invalid ranges) ----

    [Theory]
    [InlineData("1/0 cup flour", "1/0", "cup flour")]
    [InlineData("2 1/0 cups sugar", "2 1/0", "cups sugar")]
    [InlineData("3-2 cups sugar", "3-2", "cups sugar")] // shape matches a range, but the upper bound doesn't exceed the lower
    public void A_shape_that_does_not_construct_a_valid_quantity_is_flagged_not_defaulted(string line, string quantitySpan, string ingredient)
    {
        var tokens = IngredientLineTokenizer.Tokenize(line);

        Assert.NotNull(tokens.Quantity);
        Assert.True(tokens.Quantity!.IsInvalid);
        Assert.Null(tokens.Quantity.Value);
        Assert.Null(tokens.Quantity.Range);
        Assert.Equal(quantitySpan, tokens.Quantity.Span.Text);

        // Invalid quantities are never followed by a unit-candidate attempt — the whole remainder is preserved
        // as ingredient text rather than guessing where the quantity should have ended.
        Assert.Null(tokens.UnitCandidate);
        Assert.Equal(ingredient, tokens.IngredientText?.Text);

        var ambiguity = Assert.Single(tokens.Ambiguities);
        Assert.Equal(IngredientLineAmbiguityKind.InvalidQuantity, ambiguity.Kind);
        Assert.Equal(quantitySpan, ambiguity.Span.Text);
    }

    // ---- Optionality ----

    [Fact]
    public void A_trailing_parenthetical_optional_marker_is_stripped_and_flagged()
    {
        var tokens = IngredientLineTokenizer.Tokenize("1 tsp vanilla extract (optional)");

        AssertUnitAndIngredient(tokens, "tsp", "vanilla extract");
        Assert.True(tokens.IsOptional);
        Assert.Empty(tokens.PreparationText);
    }

    [Fact]
    public void A_trailing_comma_optional_marker_is_stripped_and_flagged()
    {
        var tokens = IngredientLineTokenizer.Tokenize("2 tbsp lemon juice, optional");

        AssertUnitAndIngredient(tokens, "tbsp", "lemon juice");
        Assert.True(tokens.IsOptional);
        Assert.Empty(tokens.PreparationText);
    }

    [Fact]
    public void A_line_without_an_optional_marker_is_not_flagged()
    {
        var tokens = IngredientLineTokenizer.Tokenize("1 tsp vanilla extract");

        Assert.False(tokens.IsOptional);
    }

    // ---- Preparation-clause splitting ----

    [Fact]
    public void A_single_trailing_clause_becomes_preparation_text()
    {
        var tokens = IngredientLineTokenizer.Tokenize("2 cups flour, sifted");

        AssertUnitAndIngredient(tokens, "cups", "flour");
        Assert.Equal(["sifted"], tokens.PreparationText.Select(s => s.Text));
    }

    [Fact]
    public void Multiple_trailing_clauses_are_kept_in_order()
    {
        var tokens = IngredientLineTokenizer.Tokenize("2 cups flour, sifted, cooled");

        AssertUnitAndIngredient(tokens, "cups", "flour");
        Assert.Equal(["sifted", "cooled"], tokens.PreparationText.Select(s => s.Text));
    }

    [Fact]
    public void A_comma_inside_a_parenthetical_does_not_start_a_new_clause()
    {
        var tokens = IngredientLineTokenizer.Tokenize("1 onion, diced (about 1 cup, packed)");

        AssertUnitAndIngredient(tokens, "onion", "diced (about 1 cup, packed)");
        Assert.Empty(tokens.PreparationText);
    }

    // ---- Lines the tokenizer deliberately leaves ambiguous ----

    [Fact]
    public void A_dimension_that_looks_like_digits_is_not_read_as_a_quantity()
    {
        // "9x13" fails every quantity shape — a digit run directly followed by a letter, with no
        // vocabulary to say whether that letter starts a unit or a dimension suffix, is left alone rather
        // than guessed at either way.
        var tokens = IngredientLineTokenizer.Tokenize("9x13-inch pan");

        Assert.Null(tokens.Quantity);
        Assert.Null(tokens.UnitCandidate);
        Assert.Equal("9x13-inch pan", tokens.IngredientText?.Text);

        var ambiguity = Assert.Single(tokens.Ambiguities);
        Assert.Equal(IngredientLineAmbiguityKind.NoQuantityDetected, ambiguity.Kind);
    }

    [Fact]
    public void An_adjective_is_captured_as_an_unverified_unit_candidate_without_being_flagged()
    {
        // The range is unambiguous. "large" is captured only because it is the next word — the tokenizer
        // has no way to know it is an adjective rather than a unit, and does not claim to: there is no
        // ambiguity entry for it. Rejecting it is the reference matcher's job (7.3).
        var tokens = IngredientLineTokenizer.Tokenize("1-2 large eggs");

        Assert.Equal(QuantityRange.Create(Quantity.FromInt(1), Quantity.FromInt(2)), tokens.Quantity!.Range);
        AssertUnitAndIngredient(tokens, "large", "eggs");
        Assert.Empty(tokens.Ambiguities);
    }

    [Fact]
    public void A_quantity_glued_to_a_letter_with_no_space_is_not_read_as_a_quantity()
    {
        // Without a space, "2lb" is lexically identical in shape to "9x13": digits directly followed by
        // letters. The tokenizer cannot tell a glued unit abbreviation apart from a dimension or code, so it
        // treats both the same way rather than guessing that this one is probably a unit.
        var tokens = IngredientLineTokenizer.Tokenize("2lb chicken thighs");

        Assert.Null(tokens.Quantity);
        Assert.Equal("2lb chicken thighs", tokens.IngredientText?.Text);
    }

    // ---- Original text preservation ----

    [Theory]
    [InlineData("  2 cups flour  ")]
    [InlineData("2 CUPS Flour")]
    public void The_original_text_is_preserved_exactly(string line)
    {
        var tokens = IngredientLineTokenizer.Tokenize(line);

        Assert.Equal(line, tokens.OriginalText);
    }

    [Fact]
    public void Tokenizing_is_pure_and_repeatable()
    {
        // IngredientLineTokens carries List<T>-backed properties, whose default equality is by reference —
        // so this compares the meaningful parts explicitly rather than via record equality, which would fail
        // for two structurally identical results built from two separate calls.
        const string line = "1 1/2 cups all-purpose flour, sifted (optional)";

        var first = IngredientLineTokenizer.Tokenize(line);
        var second = IngredientLineTokenizer.Tokenize(line);

        Assert.Equal(first.Quantity, second.Quantity);
        Assert.Equal(first.PackageQuantity, second.PackageQuantity);
        Assert.Equal(first.UnitCandidate, second.UnitCandidate);
        Assert.Equal(first.IngredientText, second.IngredientText);
        Assert.Equal(first.IsOptional, second.IsOptional);
        Assert.Equal(first.PreparationText, second.PreparationText);
        Assert.Equal(first.Ambiguities, second.Ambiguities);
    }
}

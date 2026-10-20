using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Modules.Ingredients.Gateways;
using CreatorPantry.Domain.Modules.Ingredients.Integration.UsdaFdc;
using CreatorPantry.Domain.Modules.Ingredients.Managers;
using CreatorPantry.Domain.Modules.Nutrition.Managers;
using CsCheck;

namespace CreatorPantry.Tests.Nutrition;

/// <summary>
/// The nutrition arithmetic (14.3, B-35): checked-in golden estimates built on recorded FoodData Central responses,
/// the rules line by line, and properties over random recipes — scaling, servings, order, partial coverage, ranges
/// and extremes.
/// </summary>
public sealed class NutritionEstimateCalculatorTests
{
    private static readonly Guid Gram = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid Cup = Guid.Parse("00000000-0000-0000-0000-0000000000a2");
    private static readonly Guid Tablespoon = Guid.Parse("00000000-0000-0000-0000-0000000000a3");
    private static readonly Guid Each = Guid.Parse("00000000-0000-0000-0000-0000000000a4");
    private static readonly Guid Clove = Guid.Parse("00000000-0000-0000-0000-0000000000a5");
    private static readonly Guid Pinch = Guid.Parse("00000000-0000-0000-0000-0000000000a6");

    private static readonly IReadOnlyDictionary<Guid, NutritionEstimateUnitInput> Units = new Dictionary<Guid, NutritionEstimateUnitInput>
    {
        [Gram] = new(Gram, MeasurementDimension.Mass, 1m),
        [Cup] = new(Cup, MeasurementDimension.Volume, 236.5882365m),
        [Tablespoon] = new(Tablespoon, MeasurementDimension.Volume, 14.78676478125m),
        [Each] = new(Each, MeasurementDimension.Count, 1m),
        [Clove] = new(Clove, MeasurementDimension.Count, 1m),
        [Pinch] = new(Pinch, MeasurementDimension.Qualitative, null),
    };

    // ---- Golden estimates, on FDC's own recorded figures ----

    private static readonly Guid Flour = Guid.Parse("00000000-0000-0000-0000-0000000000f1");
    private static readonly Guid Yogurt = Guid.Parse("00000000-0000-0000-0000-0000000000f2");
    private static readonly Guid Oil = Guid.Parse("00000000-0000-0000-0000-0000000000f3");

    [Fact]
    public void A_complete_flatbread_estimate_matches_its_golden()
    {
        var estimate = Estimate(Flatbread(withUnmappedOil: false));

        Assert.True(estimate.Publishable);
        AssertGolden("flatbread-complete", Serialize(estimate));
    }

    [Fact]
    public void A_partial_flatbread_estimate_matches_its_golden()
    {
        var estimate = Estimate(Flatbread(withUnmappedOil: true));

        Assert.False(estimate.Publishable);
        Assert.True(estimate.Partial);
        AssertGolden("flatbread-partial", Serialize(estimate));
    }

    [Fact]
    public void The_golden_flatbread_says_what_it_is_and_what_it_rests_on()
    {
        var estimate = Estimate(Flatbread(withUnmappedOil: false));

        Assert.Equal("Estimate", estimate.Label);
        Assert.Equal("nutrition-estimate/1", estimate.MethodVersion);
        Assert.Contains("no trim, refuse or cooking-loss adjustment", estimate.Method, StringComparison.Ordinal);
        Assert.Equal(NutritionQuantityBasis.CreatorPortion, estimate.Confidence);
        Assert.Equal(4m, estimate.Servings);
        var source = Assert.Single(estimate.Sources);
        Assert.Equal(("usda-fdc", "CC0-1.0"), (source.SourceCode, source.LicenseCode));

        // FDC's flour reports fibre and the yogurt does not: the total is a lower bound and names the yogurt line.
        var fibre = Assert.Single(estimate.Nutrients, nutrient => nutrient.Code == NutrientCode.DietaryFiber);
        Assert.False(fibre.Complete);
        Assert.Equal([Line(2)], fibre.LinesMissingIt);

        // 250 g + 30–60 g of flour at 364 kcal/100 g, plus a creator's 245 g cup of yogurt at 77.3185458 kcal/100 g.
        var energy = Assert.Single(estimate.Nutrients, nutrient => nutrient.Code == NutrientCode.Energy);
        Assert.Equal(1208.630437m, energy.WholeRecipe.Low);
        Assert.Equal(1317.830437m, energy.WholeRecipe.High);
        Assert.Equal((1209m, 1318m), (energy.WholeRecipe.DisplayLow, energy.WholeRecipe.DisplayHigh));
        Assert.True(energy.WholeRecipe.IsRange);
        Assert.Equal((302m, 329m), (energy.PerServing!.DisplayLow, energy.PerServing.DisplayHigh));
    }

    private static NutritionEstimateInput Flatbread(bool withUnmappedOil)
    {
        var flour = Recorded("sr-legacy-flour-169761");
        var yogurt = Recorded("foundation-yogurt-2259793");

        List<NutritionEstimateLineInput> lines =
        [
            new(Line(1), "250 g all-purpose flour", 250m, null, Gram, Flour, false),
            new(Line(2), "1 cup whole-milk yogurt", 1m, null, Cup, Yogurt, false),
            new(Line(3), "30–60 g flour, for dusting", 30m, 60m, Gram, Flour, false),
            // Optional, because that is what "to taste" is, and the creator's own flag is what says so. It
            // was `false` while an unquantified line was outside the required set either way (B-35); since
            // 14.7's H-4 the two are different things, and this line is the first kind.
            new(Line(4), "salt, to taste", null, null, null, null, true),
            new(Line(5), "1 tbsp sesame seeds (optional)", 1m, null, Tablespoon, null, true),
        ];

        if (withUnmappedOil)
        {
            lines.Add(new(Line(6), "2 tbsp olive oil", 2m, null, Tablespoon, Oil, false));
        }

        return new NutritionEstimateInput(
            Guid.Parse("00000000-0000-0000-0000-0000000000b1"),
            Guid.Parse("00000000-0000-0000-0000-0000000000b2"),
            3,
            lines,
            Units,
            new Dictionary<Guid, NutritionEstimateMappingInput>
            {
                [Flour] = Mapping(Flour, flour),
                [Yogurt] = Mapping(Yogurt, yogurt, Cup, 1m, 245m, NutritionPortionOrigin.Creator),
            },
            new Dictionary<(string, string), NutritionEstimateReferenceInput>
            {
                [(flour.SourceCode, flour.ExternalRecordId)] = flour,
                [(yogurt.SourceCode, yogurt.ExternalRecordId)] = yogurt,
            },
            new NutritionEstimateServingsInput(null, 4m, null, null));
    }

    /// <summary>A recorded FDC response, read by the real adapter, as the reference an estimate is given.</summary>
    private static NutritionEstimateReferenceInput Recorded(string fixture)
    {
        var assembly = typeof(NutritionEstimateCalculatorTests).Assembly;
        var resource = assembly.GetManifestResourceNames().Single(name => name.EndsWith($".{fixture}.fdc.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var document = JsonDocument.Parse(stream);

        Assert.Equal(UsdaFdcReadResult.Read, UsdaFdcFoodReader.ReadFood(
            document.RootElement, EvidenceSourceCatalog.UsdaFoodDataCentral, DateTimeOffset.UnixEpoch, out var food));

        var provenance = food!.Provenance;
        return new NutritionEstimateReferenceInput(
            provenance.SourceCode, provenance.ExternalRecordId, provenance.SourceVersion, provenance.Citation, provenance.LicenseCode, provenance.LicenseUrl,
            [.. food.Nutrients.Select(nutrient => new NutritionEstimateNutrientInput(nutrient.Code, nutrient.AmountPer100Grams, nutrient.Unit))]);
    }

    // ---- The rules, line by line ----

    [Fact]
    public void Lines_are_classified_in_the_order_the_rules_are_written()
    {
        var reference = Reference("1", (NutrientCode.Energy, 100m));
        var confirmed = Guid.NewGuid();
        var candidate = Guid.NewGuid();
        var review = Guid.NewGuid();
        var stale = Guid.NewGuid();
        var withdrawn = Guid.NewGuid();
        var noPortion = Guid.NewGuid();

        var input = Input(
            [
                LineOf(0, 10m, Gram, null, optional: true),
                LineOf(1, null, Gram, confirmed),
                LineOf(2, 10m, Gram, null),
                LineOf(3, 10m, Gram, Guid.NewGuid()),
                LineOf(4, 10m, Gram, candidate),
                LineOf(5, 10m, Gram, review),
                LineOf(6, 10m, Gram, stale),
                LineOf(7, 10m, Gram, withdrawn),
                LineOf(8, 10m, null, confirmed),
                LineOf(9, 10m, Guid.NewGuid(), confirmed),
                LineOf(10, 10m, Pinch, confirmed),
                LineOf(11, 1m, Cup, noPortion),
                LineOf(12, 10m, Gram, confirmed),
            ],
            [
                Mapping(confirmed, reference),
                Mapping(candidate, reference) with { Status = NutritionMappingStatus.Candidate },
                Mapping(review, reference) with { Status = NutritionMappingStatus.NeedsReview },
                Mapping(stale, reference) with { SourceVersion = "sr-legacy/2001-01-01" },
                Mapping(withdrawn, reference) with { ExternalRecordId = "gone" },
                Mapping(noPortion, reference),
            ],
            [reference]);

        var lines = Estimate(input).Lines;

        Assert.Equal(
            [
                (NutritionLineOutcome.Optional, NutritionLineReason.None, false),

                // Required, as of 14.7's H-4: an ingredient the creator did not mark optional is part of the
                // dish even when no amount is stated, so it keeps the estimate from being complete.
                (NutritionLineOutcome.NotQuantified, NutritionLineReason.None, true),
                (NutritionLineOutcome.Unmapped, NutritionLineReason.NoIngredient, true),
                (NutritionLineOutcome.Unmapped, NutritionLineReason.NoMapping, true),
                (NutritionLineOutcome.Unmapped, NutritionLineReason.MappingNotConfirmed, true),
                (NutritionLineOutcome.NeedsReview, NutritionLineReason.MappingUnderReview, true),
                (NutritionLineOutcome.NeedsReview, NutritionLineReason.SourceVersionChanged, true),
                (NutritionLineOutcome.NeedsReview, NutritionLineReason.ReferenceWithdrawn, true),
                (NutritionLineOutcome.Unconvertible, NutritionLineReason.NoUnit, true),
                (NutritionLineOutcome.Unconvertible, NutritionLineReason.UnitUnknown, true),
                (NutritionLineOutcome.Unconvertible, NutritionLineReason.UnitNotMeasurable, true),
                (NutritionLineOutcome.Unconvertible, NutritionLineReason.NoPortion, true),
                (NutritionLineOutcome.Included, NutritionLineReason.None, true),
            ],
            lines.Select(line => (line.Outcome, line.Reason, line.Required)));

        // What stands between this estimate and publishable, by why — never the optional or unquantified lines.
        var unresolved = Estimate(input).Unresolved;
        Assert.Equal([Line(2), Line(3), Line(4)], unresolved.Unmapped);
        Assert.Equal([Line(5), Line(6), Line(7)], unresolved.NeedsReview);
        Assert.Equal([Line(8), Line(9), Line(10), Line(11)], unresolved.Unconvertible);
    }

    [Fact]
    public void A_volume_converts_through_millilitres_and_a_count_only_through_its_own_unit()
    {
        var reference = Reference("1", (NutrientCode.Protein, 10m));
        var byCup = Guid.NewGuid();
        var byEach = Guid.NewGuid();

        var estimate = Estimate(Input(
            [
                LineOf(0, 2m, Tablespoon, byCup),
                LineOf(1, 3m, Each, byEach),
                LineOf(2, 3m, Clove, byEach),
            ],
            [
                Mapping(byCup, reference, Cup, 1m, 120m, NutritionPortionOrigin.Reference),
                Mapping(byEach, reference, Each, 2m, 100m, NutritionPortionOrigin.Creator),
            ],
            [reference]));

        // Two tablespoons of something a cup of which weighs 120 g: 2 × 14.787 ml ÷ 236.588 ml × 120 g = 15 g.
        Assert.Equal(15m, estimate.Lines[0].GramsLow);
        Assert.Equal(NutritionQuantityBasis.ReferencePortion, estimate.Lines[0].Basis);

        // Three of something two of which weigh 100 g.
        Assert.Equal(150m, estimate.Lines[1].GramsLow);
        Assert.Equal(NutritionQuantityBasis.CreatorPortion, estimate.Lines[1].Basis);

        // A clove is not an "each".
        Assert.Equal((NutritionLineOutcome.Unconvertible, NutritionLineReason.PortionDoesNotMatch), (estimate.Lines[2].Outcome, estimate.Lines[2].Reason));
        Assert.Equal(NutritionQuantityBasis.CreatorPortion, estimate.Confidence);
    }

    [Fact]
    public void Confidence_is_mass_when_everything_counted_was_weighed()
    {
        var reference = Reference("1", (NutrientCode.Protein, 10m));
        var mapping = Guid.NewGuid();

        var estimate = Estimate(Input([LineOf(0, 100m, Gram, mapping)], [Mapping(mapping, reference, Cup, 1m, 120m, NutritionPortionOrigin.Creator)], [reference]));

        Assert.Equal(NutritionQuantityBasis.Mass, estimate.Confidence);
    }

    [Theory]
    [InlineData(null, 6, null, null, 6, NutritionServingsSource.ServingCount)]
    [InlineData(3, 6, null, null, 3, NutritionServingsSource.Requested)]
    [InlineData(null, null, 900, 150, 6, NutritionServingsSource.YieldOverServingSize)]
    [InlineData(null, null, 900, null, null, NutritionServingsSource.None)]
    [InlineData(null, null, null, null, null, NutritionServingsSource.None)]
    public void Servings_come_from_the_caller_then_the_count_then_the_batch_over_the_serving_size(
        int? requested, int? count, int? batch, int? size, int? expected, NutritionServingsSource source)
    {
        var reference = Reference("1", (NutrientCode.Protein, 10m));
        var mapping = Guid.NewGuid();
        var input = Input([LineOf(0, 600m, Gram, mapping)], [Mapping(mapping, reference)], [reference]) with
        {
            Servings = new NutritionEstimateServingsInput(requested, count, batch, size),
        };

        var estimate = Estimate(input);

        Assert.Equal(source, estimate.ServingsSource);
        Assert.Equal(expected, (int?)estimate.Servings);
        var protein = Assert.Single(estimate.Nutrients);
        Assert.Equal(expected is null ? null : 60m / expected.Value, protein.PerServing?.Low);
    }

    [Fact]
    public void A_nutrient_one_counted_reference_does_not_report_is_a_named_lower_bound_never_a_zero()
    {
        var full = Reference("1", (NutrientCode.Protein, 10m), (NutrientCode.Sodium, 400m));
        var partial = Reference("2", (NutrientCode.Protein, 20m));
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());

        var estimate = Estimate(Input(
            [LineOf(0, 100m, Gram, a), LineOf(1, 100m, Gram, b)],
            [Mapping(a, full), Mapping(b, partial)],
            [full, partial]));

        Assert.True(estimate.Publishable);
        var sodium = Assert.Single(estimate.Nutrients, nutrient => nutrient.Code == NutrientCode.Sodium);
        Assert.False(sodium.Complete);
        Assert.Equal([Line(1)], sodium.LinesMissingIt);
        Assert.Equal(400m, sodium.WholeRecipe.Low);
        Assert.True(Assert.Single(estimate.Nutrients, nutrient => nutrient.Code == NutrientCode.Protein).Complete);
    }

    [Fact]
    public void A_recipe_with_nothing_counted_is_an_incomplete_estimate_of_nothing()
    {
        var estimate = Estimate(Input([LineOf(0, 100m, Gram, null)], [], []));

        Assert.False(estimate.Publishable);
        Assert.Empty(estimate.Nutrients);
        Assert.Null(estimate.Confidence);
        Assert.Empty(estimate.Sources);
    }

    /// <summary>
    /// A recipe nothing could be counted from is not publishable, whatever its line list says (14.7, H-4).
    /// </summary>
    /// <remarks>
    /// It used to be: `complete` was vacuously true over a list with no required lines, so an empty table of
    /// nutrients came back marked publishable. The unquantified line is now required and unresolved, and the
    /// "something was counted" floor catches the rest — including a recipe with no lines at all.
    /// </remarks>
    [Fact]
    public void A_recipe_of_only_optional_and_unquantified_lines_is_not_publishable()
    {
        var estimate = Estimate(Input([LineOf(0, 1m, Gram, null, optional: true), LineOf(1, null, null, null)], [], []));

        Assert.False(estimate.Publishable);
        Assert.True(estimate.Partial);
        Assert.Equal(NutritionEstimateStatus.Incomplete, estimate.Status);
        Assert.Empty(estimate.Nutrients);
        Assert.Single(estimate.Unresolved.NotQuantified);
    }

    [Fact]
    public void A_recipe_with_no_lines_at_all_is_not_publishable()
    {
        var estimate = Estimate(Input([], [], []));

        Assert.False(estimate.Publishable);
        Assert.Empty(estimate.Nutrients);
    }

    // ---- Extremes ----

    [Fact]
    public void A_non_zero_amount_too_small_to_show_is_trace_never_zero()
    {
        var reference = Reference("1", (NutrientCode.Sodium, 1m));
        var mapping = Guid.NewGuid();

        var estimate = Estimate(Input([LineOf(0, 0.001m, Gram, mapping)], [Mapping(mapping, reference)], [reference]));

        var sodium = Assert.Single(estimate.Nutrients).WholeRecipe;
        Assert.Equal(0.00001m, sodium.Low);
        Assert.Equal(0m, sodium.DisplayHigh);
        Assert.True(sodium.Trace);
    }

    [Fact]
    public void A_source_zero_is_a_real_zero_and_not_trace()
    {
        var reference = Reference("1", (NutrientCode.Cholesterol, 0m));
        var mapping = Guid.NewGuid();

        var amount = Assert.Single(Estimate(Input([LineOf(0, 100m, Gram, mapping)], [Mapping(mapping, reference)], [reference])).Nutrients).WholeRecipe;

        Assert.Equal(0m, amount.High);
        Assert.False(amount.Trace);
    }

    [Fact]
    public void A_total_too_large_to_state_is_refused_rather_than_thrown()
    {
        var reference = Reference("1", (NutrientCode.Energy, 900m));
        var mapping = Guid.NewGuid();

        var outcome = NutritionEstimateCalculator.Calculate(Input([LineOf(0, 1e23m, Gram, mapping)], [Mapping(mapping, reference)], [reference]));

        Assert.True(outcome.OutOfRange);
        Assert.Null(outcome.Estimate);
    }

    [Fact]
    public void The_largest_quantity_the_recipe_store_holds_is_still_stated()
    {
        var reference = Reference("1", (NutrientCode.Energy, 900m));
        var mapping = Guid.NewGuid();

        // decimal(28, 12) leaves sixteen integer digits.
        var outcome = NutritionEstimateCalculator.Calculate(Input([LineOf(0, 9_999_999_999_999_999m, Gram, mapping)], [Mapping(mapping, reference)], [reference]));

        Assert.False(outcome.OutOfRange);
    }

    [Fact]
    public void The_unit_each_nutrient_is_totalled_in_is_the_unit_the_evidence_records_it_in()
    {
        Assert.All(Enum.GetValues<NutrientCode>(), code =>
            Assert.Equal(EvidencePolicy.UnitOf(code), NutritionEstimateCalculator.EvidenceUnitOf(code)));
    }

    // ---- Properties ----

    /// <summary>Whole grams to a tenth and nutrients to a hundredth, so every exact total fits six places and comparisons are exact.</summary>
    private static readonly Gen<decimal> Grams = Gen.Int[1, 50_000].Select(i => i / 10m);

    private static readonly Gen<decimal> PerHundred = Gen.Int[0, 90_000].Select(i => i / 100m);

    private static readonly Gen<(decimal Low, decimal? High)> Amount =
        Gen.Select(Grams, Gen.Bool, Gen.Int[1, 500]).Select((low, ranged, extra) => (low, ranged ? low + extra / 10m : (decimal?)null));

    /// <summary>A recipe of weighed lines, each with its own reference reporting energy and protein.</summary>
    private static readonly Gen<NutritionEstimateInput> Recipes =
        Gen.Select(Amount, PerHundred, PerHundred).List[1, 12].Select(rows =>
        {
            var lines = new List<NutritionEstimateLineInput>();
            var mappings = new List<NutritionEstimateMappingInput>();
            var references = new List<NutritionEstimateReferenceInput>();

            for (var i = 0; i < rows.Count; i++)
            {
                var ((low, high), energy, protein) = rows[i];
                var reference = Reference($"{i}", (NutrientCode.Energy, energy), (NutrientCode.Protein, protein));
                var ingredient = Guid.NewGuid();

                lines.Add(new NutritionEstimateLineInput(Line(i), $"line {i}", low, high, Gram, ingredient, false));
                mappings.Add(Mapping(ingredient, reference));
                references.Add(reference);
            }

            return Input(lines, mappings, references);
        });

    [Fact]
    public void Scaling_every_quantity_by_k_scales_every_whole_recipe_total_by_exactly_k()
    {
        Gen.Select(Recipes, Gen.Int[1, 20]).Sample((recipe, k) =>
        {
            var scaled = recipe with
            {
                Lines = [.. recipe.Lines.Select(line => line with { Quantity = line.Quantity * k, QuantityUpper = line.QuantityUpper * k })],
            };

            var before = Estimate(recipe).Nutrients;
            var after = Estimate(scaled).Nutrients;

            return before.Zip(after).All(pair =>
                pair.First.WholeRecipe.Low * k == pair.Second.WholeRecipe.Low
                && pair.First.WholeRecipe.High * k == pair.Second.WholeRecipe.High);
        });
    }

    [Fact]
    public void Per_serving_times_servings_is_the_whole_recipe_within_the_rounding_of_six_places()
    {
        Gen.Select(Recipes, Gen.Int[1, 24]).Sample((recipe, servings) =>
        {
            var estimate = Estimate(recipe with { Servings = new NutritionEstimateServingsInput(servings, null, null, null) });

            return estimate.Nutrients.All(nutrient =>
                Math.Abs(nutrient.PerServing!.High * servings - nutrient.WholeRecipe.High) <= servings * 0.0000005m);
        });
    }

    [Fact]
    public void The_order_of_the_lines_never_changes_a_total()
    {
        Gen.Select(Recipes, Gen.Int[0, 1000]).Sample((recipe, seed) =>
        {
            var shuffled = recipe with { Lines = [.. recipe.Lines.OrderBy(_ => new Random(seed).Next())] };

            return Totals(Estimate(recipe)).SequenceEqual(Totals(Estimate(shuffled)));
        });
    }

    [Fact]
    public void An_unmapped_required_line_never_changes_a_total_and_always_makes_the_estimate_unpublishable()
    {
        Recipes.Sample(recipe =>
        {
            var withGap = recipe with { Lines = [.. recipe.Lines, new NutritionEstimateLineInput(Guid.NewGuid(), "olive oil", 2m, null, Tablespoon, Guid.NewGuid(), false)] };

            var before = Estimate(recipe);
            var after = Estimate(withGap);

            return before.Publishable
                && !after.Publishable
                && after.Partial
                && Totals(before).SequenceEqual(Totals(after));
        });
    }

    [Fact]
    public void An_optional_line_never_changes_a_total_or_whether_it_is_publishable()
    {
        Recipes.Sample(recipe =>
        {
            var mapped = recipe.Lines[0].IngredientId;
            var withGarnish = recipe with
            {
                Lines =
                [
                    .. recipe.Lines,
                    new NutritionEstimateLineInput(Guid.NewGuid(), "a garnish", 50m, null, Gram, mapped, true),
                ],
            };

            var before = Estimate(recipe);
            var after = Estimate(withGarnish);

            return before.Publishable == after.Publishable && Totals(before).SequenceEqual(Totals(after));
        });
    }

    /// <summary>
    /// A required line with no amount changes nothing about the totals, and everything about whether they may
    /// be published (14.7, H-4).
    /// </summary>
    /// <remarks>
    /// This is the amendment to B-35. "Salt to taste" is the creator's own marking and stays optional; "butter,
    /// softened" with no amount is an ingredient of the dish that nothing can weigh, and an estimate that
    /// called itself Complete while omitting it was the audit's finding. The totals are unchanged because
    /// there is still nothing to add; what changes is the claim made about them.
    /// </remarks>
    [Fact]
    public void A_required_line_with_no_amount_keeps_the_estimate_from_being_publishable()
    {
        Recipes.Sample(recipe =>
        {
            var withUnweighable = recipe with
            {
                Lines =
                [
                    .. recipe.Lines,
                    new NutritionEstimateLineInput(Guid.NewGuid(), "butter, softened", null, null, null, null, false),
                ],
            };

            var before = Estimate(recipe);
            var after = Estimate(withUnweighable);

            return !after.Publishable
                && after.Partial
                && after.Unresolved.NotQuantified.Count == 1
                && Totals(before).SequenceEqual(Totals(after));
        });
    }

    [Fact]
    public void The_low_end_never_exceeds_the_high_end_and_a_range_is_said_to_be_one()
    {
        Recipes.Sample(recipe =>
        {
            var estimate = Estimate(recipe);
            var anyRange = recipe.Lines.Any(line => line.QuantityUpper is not null);

            return estimate.Nutrients.All(nutrient =>
                nutrient.WholeRecipe.Low <= nutrient.WholeRecipe.High
                && nutrient.WholeRecipe.DisplayLow <= nutrient.WholeRecipe.DisplayHigh
                && (nutrient.WholeRecipe.IsRange || nutrient.WholeRecipe.Low == nutrient.WholeRecipe.High)
                && (anyRange || !nutrient.WholeRecipe.IsRange));
        });
    }

    [Fact]
    public void Every_display_value_is_its_exact_value_rounded_once_with_ties_to_even()
    {
        Recipes.Sample(recipe => Estimate(recipe).Nutrients.All(nutrient =>
            nutrient.WholeRecipe.DisplayHigh == Math.Round(nutrient.WholeRecipe.High, nutrient.WholeRecipe.DisplayPrecision, MidpointRounding.ToEven)
            && nutrient.WholeRecipe.DisplayPrecision == NutritionEstimatePolicy.DisplayPrecisionOf(nutrient.Unit)));
    }

    // ---- Helpers ----

    private static NutritionEstimateServiceModel Estimate(NutritionEstimateInput input) =>
        NutritionEstimateCalculator.Calculate(input).Estimate ?? throw new InvalidOperationException("Out of range.");

    private static IEnumerable<(NutrientCode, decimal, decimal)> Totals(NutritionEstimateServiceModel estimate) =>
        estimate.Nutrients.Select(nutrient => (nutrient.Code, nutrient.WholeRecipe.Low, nutrient.WholeRecipe.High));

    private static Guid Line(int index) => new($"00000000-0000-0000-0001-{index:D12}");

    private static NutritionEstimateLineInput LineOf(int index, decimal? quantity, Guid? unit, Guid? ingredient, bool optional = false) =>
        new(Line(index), $"line {index}", quantity, null, unit, ingredient, optional);

    private static NutritionEstimateReferenceInput Reference(string id, params (NutrientCode Code, decimal Per100)[] nutrients) => new(
        "usda-fdc",
        id,
        "sr-legacy/2019-04-01",
        EvidenceSourceCatalog.UsdaFoodDataCentral.Citation,
        "CC0-1.0",
        EvidenceSourceCatalog.UsdaFoodDataCentral.LicenseUrl,
        [.. nutrients.Select(nutrient => new NutritionEstimateNutrientInput(nutrient.Code, nutrient.Per100, NutritionEstimateCalculator.EvidenceUnitOf(nutrient.Code)))]);

    private static NutritionEstimateMappingInput Mapping(
        Guid ingredient,
        NutritionEstimateReferenceInput reference,
        Guid? portionUnit = null,
        decimal? portionAmount = null,
        decimal? portionGrams = null,
        NutritionPortionOrigin? origin = null) =>
        new(
            new Guid(ingredient.ToByteArray().Reverse().ToArray()),
            ingredient,
            NutritionMappingStatus.Confirmed,
            reference.SourceCode,
            reference.ExternalRecordId,
            reference.SourceVersion,
            portionUnit,
            portionAmount,
            portionGrams,
            origin);

    private static NutritionEstimateInput Input(
        IReadOnlyList<NutritionEstimateLineInput> lines,
        IReadOnlyList<NutritionEstimateMappingInput> mappings,
        IReadOnlyList<NutritionEstimateReferenceInput> references) =>
        new(
            Guid.Parse("00000000-0000-0000-0000-0000000000c1"),
            Guid.Parse("00000000-0000-0000-0000-0000000000c2"),
            1,
            lines,
            Units,
            mappings.ToDictionary(mapping => mapping.IngredientId),
            references.ToDictionary(reference => (reference.SourceCode, reference.ExternalRecordId)),
            new NutritionEstimateServingsInput(null, null, null, null));

    // ---- Golden plumbing ----

    private const string GoldenDirectory = "EstimateGolden";

    private static readonly JsonSerializerOptions GoldenOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static string Serialize(NutritionEstimateServiceModel estimate) => JsonSerializer.Serialize(estimate, GoldenOptions);

    private static void AssertGolden(string name, string actual, [CallerFilePath] string sourceFile = "")
    {
        var normalized = actual.ReplaceLineEndings("\n") + "\n";

        if (Environment.GetEnvironmentVariable("UPDATE_ESTIMATE_GOLDEN") == "1")
        {
            var directory = Path.Combine(Path.GetDirectoryName(sourceFile)!, GoldenDirectory);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, $"{name}.golden.json"), normalized);
            return;
        }

        var assembly = typeof(NutritionEstimateCalculatorTests).Assembly;
        var resource = assembly.GetManifestResourceNames()
            .Single(n => n.EndsWith($"{GoldenDirectory}.{name}.golden.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream);
        Assert.Equal(reader.ReadToEnd().ReplaceLineEndings("\n"), normalized);
    }
}

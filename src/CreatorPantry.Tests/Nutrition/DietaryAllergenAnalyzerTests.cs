using System.Text.Json;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Ingredients.Managers;
using CreatorPantry.Domain.Modules.Nutrition.Business;
using CreatorPantry.Domain.Modules.Nutrition.Managers;
using CreatorPantry.Domain.Modules.Recipes.Managers;

namespace CreatorPantry.Tests.Nutrition;

/// <summary>
/// The dietary and allergen decision table (14.4, B-36), case by case: positive, negative, unknown, conflicting,
/// hidden-source and cross-contact — and the guard that nothing in it can say "safe".
/// </summary>
public sealed class DietaryAllergenAnalyzerTests
{
    private static readonly AnalysisSubject Milk = new(Guid.Parse("00000000-0000-0000-0000-00000000a001"), "Milk");
    private static readonly AnalysisSubject Wheat = new(Guid.Parse("00000000-0000-0000-0000-00000000a002"), "Wheat");
    private static readonly AnalysisSubject Vegan = new(Guid.Parse("00000000-0000-0000-0000-00000000d001"), "Vegan");
    private static readonly AnalysisSubject GlutenFree = new(Guid.Parse("00000000-0000-0000-0000-00000000d002"), "Gluten-Free");

    private static readonly TraitSourceServiceModel Official =
        new(Guid.Parse("00000000-0000-0000-0000-00000000c001"), "official-list", "An official list", ReferenceSourceKind.OfficialDatabase, "Official list, 2026.");

    private static readonly TraitSourceServiceModel Publisher =
        new(Guid.Parse("00000000-0000-0000-0000-00000000c002"), "a-publisher", "A publisher", ReferenceSourceKind.Publisher, "A publisher's tables.");

    private static readonly Guid Flour = Guid.Parse("00000000-0000-0000-0000-0000000f0001");
    private static readonly Guid OliveOil = Guid.Parse("00000000-0000-0000-0000-0000000f0002");
    private static readonly Guid SoySauce = Guid.Parse("00000000-0000-0000-0000-0000000f0003");
    private static readonly Guid Oats = Guid.Parse("00000000-0000-0000-0000-0000000f0004");
    private static readonly Guid Sugar = Guid.Parse("00000000-0000-0000-0000-0000000f0005");
    private static readonly Guid Mystery = Guid.Parse("00000000-0000-0000-0000-0000000f0006");

    private static readonly DateOnly Today = new(2037, 10, 20);

    // ---- Positive ----

    [Fact]
    public void An_approved_present_trait_means_the_recipe_contains_the_allergen_and_cites_its_source()
    {
        var analysis = Analyze([Line(1, Flour)], allergens: [Wheat], evidence: Evidence(Allergen(Flour, Wheat, AllergenPresence.Present, Official)));

        var wheat = Assert.Single(analysis.Allergens);
        Assert.Equal(RecipeAllergenFinding.Contains, wheat.Finding);
        Assert.Equal([LineId(1)], wheat.ContainingLines);
        Assert.Equal("Contains Wheat, according to the cited records.", wheat.Statement);

        var evidence = Assert.Single(Assert.Single(wheat.Lines).Evidence);
        Assert.Equal((AllergenPresence.Present, "official-list", "Official list, 2026."), (evidence.Presence, evidence.SourceCode, evidence.Citation));
    }

    // ---- Negative ----

    [Fact]
    public void Every_line_not_listed_is_a_statement_about_the_records_and_never_that_the_recipe_is_free_of_it()
    {
        var analysis = Analyze(
            [Line(1, OliveOil)],
            allergens: [Milk],
            evidence: Evidence(Allergen(OliveOil, Milk, AllergenPresence.NotListedBySource, Official, "Composition published without milk.")));

        var milk = Assert.Single(analysis.Allergens);
        Assert.Equal(RecipeAllergenFinding.NotListedInRecords, milk.Finding);
        Assert.Equal(AllergenLineFinding.NotListed, Assert.Single(milk.Lines).Finding);
        Assert.Equal("Milk is not listed in the cited records for any ingredient. This is not a statement that the recipe is free of Milk.", milk.Statement);
        Assert.Equal(DietaryAllergenAnalysisPolicy.CrossContactDisclaimer, analysis.CrossContactDisclaimer);
    }

    // ---- Unknown ----

    [Fact]
    public void No_record_an_unrecognised_line_and_a_source_that_does_not_say_are_all_unknown_never_absent()
    {
        var analysis = Analyze(
            [Line(1, Mystery), Line(2, null, text: "a splash of house sauce"), Line(3, OliveOil)],
            allergens: [Milk],
            profiles: [Vegan],
            evidence: Evidence(
                Allergen(OliveOil, Milk, AllergenPresence.Unknown, Publisher, "Not stated."),
                Allergen(Flour, Milk, AllergenPresence.Present, Official)));

        var milk = Assert.Single(analysis.Allergens);
        Assert.Equal(RecipeAllergenFinding.Unknown, milk.Finding);
        Assert.Equal([LineId(1), LineId(2), LineId(3)], milk.UnknownLines);
        Assert.Equal(
            [TraitFindingReason.NoRecord, TraitFindingReason.NotARecognisedIngredient, TraitFindingReason.NoRecord],
            milk.Lines.Select(line => line.Reason));

        // The unrecognised line's own words are never read: "house sauce" names nothing.
        Assert.Empty(milk.Lines[1].Evidence);
        Assert.Equal(RecipeDietaryFinding.Unknown, Assert.Single(analysis.Dietary).Finding);
    }

    [Fact]
    public void A_recipe_with_no_lines_is_unknown_not_listed_or_fitting()
    {
        var analysis = Analyze([], allergens: [Milk], profiles: [Vegan], evidence: Evidence());

        Assert.Equal(RecipeAllergenFinding.Unknown, Assert.Single(analysis.Allergens).Finding);
        Assert.Equal(RecipeDietaryFinding.Unknown, Assert.Single(analysis.Dietary).Finding);
    }

    // ---- Conflicting ----

    [Fact]
    public void Sources_that_disagree_give_the_more_cautious_finding_and_both_are_cited()
    {
        var analysis = Analyze(
            [Line(1, Flour), Line(2, Oats)],
            allergens: [Wheat],
            profiles: [Vegan],
            evidence: Evidence(
                Allergen(Flour, Wheat, AllergenPresence.Present, Publisher),
                Allergen(Flour, Wheat, AllergenPresence.NotListedBySource, Official, "Not in this listing."),
                Allergen(Oats, Wheat, AllergenPresence.PossiblePresence, Publisher, "May be milled alongside wheat."),
                Allergen(Oats, Wheat, AllergenPresence.NotListedBySource, Official, "Not in this listing."),
                Dietary(Flour, Vegan, DietaryCompatibility.Compatible, Official),
                Dietary(Flour, Vegan, DietaryCompatibility.Incompatible, Publisher, "Enriched with an animal-derived vitamin."),
                Dietary(Oats, Vegan, DietaryCompatibility.Compatible, Official),
                Dietary(Oats, Vegan, DietaryCompatibility.DependsOnProduct, Publisher, "Some are processed with honey.")));

        var wheat = Assert.Single(analysis.Allergens);
        Assert.Equal(RecipeAllergenFinding.Contains, wheat.Finding);
        Assert.Equal((AllergenLineFinding.Contains, true, 2), (wheat.Lines[0].Finding, wheat.Lines[0].Conflict, wheat.Lines[0].Evidence.Count));
        Assert.Equal((AllergenLineFinding.Possible, true), (wheat.Lines[1].Finding, wheat.Lines[1].Conflict));
        Assert.Equal([LineId(1), LineId(2)], wheat.ConflictingLines);

        var vegan = Assert.Single(analysis.Dietary);
        Assert.Equal(RecipeDietaryFinding.DoesNotFit, vegan.Finding);
        Assert.Equal((DietaryLineFinding.Incompatible, true), (vegan.Lines[0].Finding, vegan.Lines[0].Conflict));
        Assert.Equal((DietaryLineFinding.DependsOnProduct, true), (vegan.Lines[1].Finding, vegan.Lines[1].Conflict));
    }

    [Fact]
    public void Two_sources_that_both_say_present_or_possible_do_not_conflict()
    {
        var analysis = Analyze(
            [Line(1, Flour)],
            allergens: [Wheat],
            evidence: Evidence(
                Allergen(Flour, Wheat, AllergenPresence.Present, Official),
                Allergen(Flour, Wheat, AllergenPresence.PossiblePresence, Publisher, "Variable supply.")));

        Assert.False(Assert.Single(Assert.Single(analysis.Allergens).Lines).Conflict);
    }

    // ---- Hidden sources ----

    [Fact]
    public void A_source_stated_possibility_and_a_product_dependent_fit_are_hidden_sources_quoting_the_source()
    {
        var analysis = Analyze(
            [Line(1, OliveOil), Line(2, SoySauce), Line(3, Sugar)],
            allergens: [Wheat],
            profiles: [GlutenFree, Vegan],
            evidence: Evidence(
                Allergen(OliveOil, Wheat, AllergenPresence.NotListedBySource, Official, "Not in this listing."),
                Allergen(SoySauce, Wheat, AllergenPresence.PossiblePresence, Official, "Traditionally brewed with wheat; tamari usually is not."),
                Allergen(Sugar, Wheat, AllergenPresence.NotListedBySource, Official, "Not in this listing."),
                Dietary(OliveOil, GlutenFree, DietaryCompatibility.Compatible, Official),
                Dietary(SoySauce, GlutenFree, DietaryCompatibility.DependsOnProduct, Official, "Depends on whether it is brewed with wheat."),
                Dietary(Sugar, GlutenFree, DietaryCompatibility.Compatible, Official),
                Dietary(OliveOil, Vegan, DietaryCompatibility.Compatible, Official),
                Dietary(SoySauce, Vegan, DietaryCompatibility.Compatible, Official),
                Dietary(Sugar, Vegan, DietaryCompatibility.DependsOnProduct, Official, "Some cane sugar is filtered through bone char.")));

        var wheat = Assert.Single(analysis.Allergens);
        Assert.Equal(RecipeAllergenFinding.Possible, wheat.Finding);
        Assert.Equal([LineId(2)], wheat.PossibleSources);
        Assert.Equal("Traditionally brewed with wheat; tamari usually is not.", Assert.Single(wheat.Lines[1].Evidence).EvidenceNote);

        Assert.Equal(
            [(GlutenFree.Name, RecipeDietaryFinding.DependsOnProduct, LineId(2)), (Vegan.Name, RecipeDietaryFinding.DependsOnProduct, LineId(3))],
            analysis.Dietary.Select(profile => (profile.DietaryProfileName, profile.Finding, Assert.Single(profile.DependsOnProductLines))));
    }

    // ---- Cross-contact ----

    [Fact]
    public void Cross_contact_is_reported_only_where_a_source_states_it_and_the_disclaimer_is_on_every_analysis()
    {
        var crossContact = Analyze(
            [Line(1, Oats)],
            allergens: [Wheat],
            evidence: Evidence(Allergen(Oats, Wheat, AllergenPresence.PossiblePresence, Official, "Commonly milled alongside wheat.")));

        var wheat = Assert.Single(crossContact.Allergens);
        Assert.Equal(RecipeAllergenFinding.Possible, wheat.Finding);
        Assert.Equal("Commonly milled alongside wheat.", Assert.Single(Assert.Single(wheat.Lines).Evidence).EvidenceNote);

        // The best answer the table can give still carries the disclaimer, word for word.
        var allClear = Analyze(
            [Line(1, OliveOil)],
            allergens: [Milk],
            profiles: [Vegan],
            evidence: Evidence(
                Allergen(OliveOil, Milk, AllergenPresence.NotListedBySource, Official, "Not in this listing."),
                Dietary(OliveOil, Vegan, DietaryCompatibility.Compatible, Official)));

        Assert.Equal(RecipeAllergenFinding.NotListedInRecords, Assert.Single(allClear.Allergens).Finding);
        Assert.Equal(RecipeDietaryFinding.FitsAsRecorded, Assert.Single(allClear.Dietary).Finding);
        Assert.All([crossContact, allClear], analysis =>
        {
            Assert.Equal(DietaryAllergenAnalysisPolicy.CrossContactDisclaimer, analysis.CrossContactDisclaimer);
            Assert.Contains("shared equipment", analysis.CrossContactDisclaimer, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Nothing_is_inferred_from_neighbours_or_category_only_from_the_ingredients_own_records()
    {
        // Flour beside the oats says nothing about the oats.
        var analysis = Analyze(
            [Line(1, Flour), Line(2, Oats)],
            allergens: [Wheat],
            evidence: Evidence(Allergen(Flour, Wheat, AllergenPresence.Present, Official)));

        var oats = Assert.Single(analysis.Allergens).Lines[1];
        Assert.Equal((AllergenLineFinding.Unknown, TraitFindingReason.NoRecord), (oats.Finding, oats.Reason));
    }

    // ---- Aggregation ----

    [Fact]
    public void The_recipe_takes_the_most_cautious_line_and_an_optional_line_still_counts()
    {
        var analysis = Analyze(
            [Line(1, OliveOil), Line(2, Mystery), Line(3, Flour, optional: true)],
            allergens: [Wheat],
            evidence: Evidence(
                Allergen(OliveOil, Wheat, AllergenPresence.NotListedBySource, Official, "Not in this listing."),
                Allergen(Flour, Wheat, AllergenPresence.Present, Official)));

        var wheat = Assert.Single(analysis.Allergens);
        Assert.Equal(RecipeAllergenFinding.Contains, wheat.Finding);
        Assert.Equal([LineId(3)], wheat.ContainingLines);
        Assert.True(wheat.Lines[2].IsOptional);
        Assert.Equal([LineId(2)], wheat.UnknownLines);
    }

    [Theory]
    [InlineData(new[] { DietaryCompatibility.Compatible, DietaryCompatibility.Compatible }, RecipeDietaryFinding.FitsAsRecorded)]
    [InlineData(new[] { DietaryCompatibility.Compatible, DietaryCompatibility.Unknown }, RecipeDietaryFinding.Unknown)]
    [InlineData(new[] { DietaryCompatibility.DependsOnProduct, DietaryCompatibility.Unknown }, RecipeDietaryFinding.DependsOnProduct)]
    [InlineData(new[] { DietaryCompatibility.Incompatible, DietaryCompatibility.DependsOnProduct }, RecipeDietaryFinding.DoesNotFit)]
    public void A_dietary_profile_is_the_most_cautious_of_its_lines(DietaryCompatibility[] lines, RecipeDietaryFinding expected)
    {
        var ingredients = new[] { Flour, OliveOil };
        var analysis = Analyze(
            [Line(1, Flour), Line(2, OliveOil)],
            profiles: [Vegan],
            evidence: Evidence([.. lines.Select((state, i) => Dietary(ingredients[i], Vegan, state, Official, "Noted."))]));

        Assert.Equal(expected, Assert.Single(analysis.Dietary).Finding);
    }

    [Fact]
    public void The_wheat_allergen_and_the_gluten_free_profile_never_answer_for_each_other()
    {
        var analysis = Analyze(
            [Line(1, Flour), Line(2, OliveOil)],
            allergens: [Wheat],
            profiles: [GlutenFree],
            evidence: Evidence(
                Allergen(Flour, Wheat, AllergenPresence.Present, Official),
                Dietary(OliveOil, GlutenFree, DietaryCompatibility.Compatible, Official)));

        // Flour contains wheat, but nothing recorded says what flour is against the gluten-free profile; olive oil
        // fits gluten-free, but that says nothing about wheat in it.
        var glutenFree = Assert.Single(analysis.Dietary);
        Assert.Equal((DietaryLineFinding.Unknown, TraitFindingReason.NoRecord), (glutenFree.Lines[0].Finding, glutenFree.Lines[0].Reason));
        Assert.Equal(AllergenLineFinding.Unknown, Assert.Single(analysis.Allergens).Lines[1].Finding);
    }

    [Fact]
    public void The_same_records_are_the_same_analysis_whatever_order_they_arrive_in()
    {
        var traits = new[]
        {
            Allergen(Flour, Wheat, AllergenPresence.Present, Official),
            Allergen(Oats, Wheat, AllergenPresence.PossiblePresence, Publisher, "Shared mill."),
            Allergen(Oats, Wheat, AllergenPresence.NotListedBySource, Official, "Not in this listing."),
        };

        var forwards = Analyze([Line(1, Flour), Line(2, Oats)], allergens: [Wheat, Milk], evidence: Evidence(traits));
        var backwards = Analyze([Line(1, Flour), Line(2, Oats)], allergens: [Milk, Wheat], evidence: Evidence([.. traits.Reverse()]));

        Assert.Equal(JsonSerializer.Serialize(forwards), JsonSerializer.Serialize(backwards));
        Assert.Equal(["Milk", "Wheat"], forwards.Allergens.Select(allergen => allergen.AllergenName));
        Assert.Equal((DietaryAllergenAnalysisPolicy.MethodVersion, Today), (forwards.MethodVersion, forwards.EvaluatedOn));
    }

    // ---- Who may reassure ----

    private static readonly TraitSourceServiceModel Community =
        new(Guid.Parse("00000000-0000-0000-0000-00000000c003"), "a-community", "A community list", ReferenceSourceKind.CommunityContributed, "Contributed.");

    private static readonly TraitSourceServiceModel Retired = Official with
    {
        ReferenceSourceId = Guid.Parse("00000000-0000-0000-0000-00000000c004"),
        Code = "retired-list",
        IsActive = false,
    };

    [Fact]
    public void A_reassuring_word_from_an_unvetted_or_retired_source_is_cited_but_never_counted()
    {
        var analysis = Analyze(
            [Line(1, OliveOil), Line(2, Sugar)],
            allergens: [Milk],
            profiles: [Vegan],
            evidence: Evidence(
                // The database refuses an unvetted "not listed"; the analyzer refuses it again, and a retired one too.
                Allergen(OliveOil, Milk, AllergenPresence.NotListedBySource, Community, "Not in our list."),
                Allergen(Sugar, Milk, AllergenPresence.NotListedBySource, Retired, "Not in this listing."),
                Dietary(OliveOil, Vegan, DietaryCompatibility.Compatible, Community),
                Dietary(Sugar, Vegan, DietaryCompatibility.Compatible, Retired)));

        var milk = Assert.Single(analysis.Allergens);
        Assert.Equal(RecipeAllergenFinding.Unknown, milk.Finding);
        Assert.All(milk.Lines, line =>
        {
            Assert.Equal(AllergenLineFinding.Unknown, line.Finding);
            Assert.False(Assert.Single(line.Evidence).Counted);
        });

        // No "every ingredient is recorded as compatible" on the word of a contributor or a retired source.
        var vegan = Assert.Single(analysis.Dietary);
        Assert.Equal(RecipeDietaryFinding.Unknown, vegan.Finding);
        Assert.All(vegan.Lines, line => Assert.False(Assert.Single(line.Evidence).Counted));
    }

    [Fact]
    public void A_cautious_word_counts_from_any_approved_source()
    {
        var analysis = Analyze(
            [Line(1, Flour), Line(2, Sugar)],
            allergens: [Wheat],
            profiles: [Vegan],
            evidence: Evidence(
                Allergen(Flour, Wheat, AllergenPresence.Present, Community),
                Allergen(Sugar, Wheat, AllergenPresence.PossiblePresence, Retired, "Shared line."),
                Dietary(Flour, Vegan, DietaryCompatibility.Incompatible, Retired, "Enriched with an animal-derived vitamin.")));

        Assert.Equal(RecipeAllergenFinding.Contains, Assert.Single(analysis.Allergens).Finding);
        Assert.Equal([LineId(2)], Assert.Single(analysis.Allergens).PossibleSources);
        Assert.Equal(RecipeDietaryFinding.DoesNotFit, Assert.Single(analysis.Dietary).Finding);
    }

    [Fact]
    public void Who_may_reassure_is_who_the_trait_schema_lets_assert_absence_and_only_while_active()
    {
        Assert.All(Enum.GetValues<ReferenceSourceKind>(), kind =>
        {
            Assert.Equal(TraitPolicy.MayAssertAllergenAbsence(kind), DietaryAllergenAnalysisPolicy.MayReassure(kind, isActive: true));
            Assert.False(DietaryAllergenAnalysisPolicy.MayReassure(kind, isActive: false));
        });
    }

    [Fact]
    public void A_finding_drawn_from_some_lines_says_how_many_others_have_no_record()
    {
        var analysis = Analyze(
            [Line(1, Flour), Line(2, Mystery), Line(3, null)],
            allergens: [Wheat],
            evidence: Evidence(Allergen(Flour, Wheat, AllergenPresence.Present, Official)));

        Assert.Equal(
            "Contains Wheat, according to the cited records. 2 other ingredients have no reviewed record.",
            Assert.Single(analysis.Allergens).Statement);
    }

    // ---- Confidence and coverage (14.6): how complete the records are, never how likely the finding ----

    [Fact]
    public void Every_line_with_a_counted_record_and_no_disagreement_is_recorded()
    {
        var milk = Assert.Single(Analyze(
            [Line(1, Flour), Line(2, OliveOil)],
            allergens: [Milk],
            evidence: Evidence(
                Allergen(Flour, Milk, AllergenPresence.NotListedBySource, Official, "Not in this composition."),
                Allergen(OliveOil, Milk, AllergenPresence.NotListedBySource, Official, "Not in this composition."))).Allergens);

        Assert.Equal((AnalysisConfidence.Recorded, new AnalysisCoverageServiceModel(2, 2)), (milk.Confidence, milk.Coverage));
    }

    [Fact]
    public void Some_lines_without_records_are_partial_and_the_finding_still_stands()
    {
        var wheat = Assert.Single(Analyze(
            [Line(1, Flour), Line(2, Mystery), Line(3, null, text: "a pinch of something")],
            allergens: [Wheat],
            evidence: Evidence(Allergen(Flour, Wheat, AllergenPresence.Present, Official))).Allergens);

        Assert.Equal(RecipeAllergenFinding.Contains, wheat.Finding);
        Assert.Equal((AnalysisConfidence.Partial, new AnalysisCoverageServiceModel(3, 1)), (wheat.Confidence, wheat.Coverage));
    }

    [Fact]
    public void Disagreeing_sources_are_disputed()
    {
        var milk = Assert.Single(Analyze(
            [Line(1, Flour)],
            allergens: [Milk],
            evidence: Evidence(
                Allergen(Flour, Milk, AllergenPresence.Present, Publisher),
                Allergen(Flour, Milk, AllergenPresence.NotListedBySource, Official, "Not in this composition."))).Allergens);

        Assert.Equal((AnalysisConfidence.Disputed, new AnalysisCoverageServiceModel(1, 1)), (milk.Confidence, milk.Coverage));
    }

    [Fact]
    public void No_counted_record_and_no_lines_at_all_are_both_none()
    {
        var unread = Assert.Single(Analyze([Line(1, Mystery)], profiles: [Vegan]).Dietary);
        Assert.Equal((AnalysisConfidence.None, new AnalysisCoverageServiceModel(1, 0)), (unread.Confidence, unread.Coverage));

        var empty = Assert.Single(Analyze([], allergens: [Milk]).Allergens);
        Assert.Equal((AnalysisConfidence.None, new AnalysisCoverageServiceModel(0, 0)), (empty.Confidence, empty.Coverage));
    }

    [Fact]
    public void A_reassuring_word_that_does_not_count_does_not_count_as_coverage_either()
    {
        var vegan = Assert.Single(Analyze(
            [Line(1, Sugar)],
            profiles: [Vegan],
            evidence: Evidence(Dietary(Sugar, Vegan, DietaryCompatibility.Compatible, Community))).Dietary);

        Assert.Equal((RecipeDietaryFinding.Unknown, AnalysisConfidence.None), (vegan.Finding, vegan.Confidence));
    }

    // ---- Sources (14.6): every source cited, once, with its licence and the latest record read ----

    [Fact]
    public void Every_source_cited_is_listed_once_by_code_with_licence_and_latest_effective_date()
    {
        var licensed = Official with { LicenseCode = "CC0-1.0", LicenseUrl = "https://creativecommons.org/publicdomain/zero/1.0/" };

        var analysis = Analyze(
            [Line(1, Flour), Line(2, Sugar)],
            allergens: [Wheat],
            profiles: [Vegan],
            evidence: Evidence(
                new ApprovedAllergenTraitServiceModel(Flour, Wheat.Id, AllergenPresence.Present, "Stated.", new DateOnly(2025, 3, 1), licensed),
                new ApprovedDietaryTraitServiceModel(Sugar, Vegan.Id, DietaryCompatibility.DependsOnProduct, "Bone char.", new DateOnly(2026, 6, 1), licensed),
                Dietary(Sugar, Vegan, DietaryCompatibility.Compatible, Community)));

        Assert.Equal(["a-community", "official-list"], analysis.Sources.Select(source => source.Code));

        var official = analysis.Sources[1];
        Assert.Equal(
            ("An official list", ReferenceSourceKind.OfficialDatabase, "CC0-1.0", true, new DateOnly(2026, 6, 1)),
            (official.Name, official.Kind, official.LicenseCode, official.IsActive, official.LatestEffectiveFrom));

        // Cited even though its word did not count: a reader can see every source the analysis read.
        Assert.Null(analysis.Sources[0].LicenseCode);
    }

    [Fact]
    public void An_analysis_with_no_records_cites_no_sources()
    {
        Assert.Empty(Analyze([Line(1, Mystery)], allergens: [Milk]).Sources);
    }

    // ---- Nothing here can say "safe" ----

    [Fact]
    public void No_finding_has_a_safe_free_certified_or_suitable_member()
    {
        var names = new[] { typeof(AllergenLineFinding), typeof(DietaryLineFinding), typeof(RecipeAllergenFinding), typeof(RecipeDietaryFinding), typeof(AnalysisConfidence) }
            .SelectMany(Enum.GetNames);

        Assert.All(names, name => Assert.DoesNotMatch("(?i)safe|free|certif|suitab|guarantee", name));
    }

    [Theory]
    [InlineData("Milk", "Vegan")]
    [InlineData("Tree Nuts", "Nut-Free")]
    [InlineData("Wheat", "Gluten-Free")]
    public void No_statement_claims_safety_and_free_of_appears_only_where_it_is_denied(string allergen, string profile)
    {
        var statements = Enum.GetValues<RecipeAllergenFinding>().SelectMany(finding => new[] { 0, 1, 3 }.Select(unknown => DietaryAllergenAnalysisPolicy.Statement(finding, allergen, unknown)))
            .Concat(Enum.GetValues<RecipeDietaryFinding>().SelectMany(finding => new[] { 0, 1, 3 }.Select(unknown => DietaryAllergenAnalysisPolicy.Statement(finding, profile, unknown))))
            .Append(DietaryAllergenAnalysisPolicy.CrossContactDisclaimer);

        Assert.All(statements, statement =>
        {
            Assert.DoesNotMatch("(?i)\\bsafe|certified|hypoallergenic|allergen-free", statement);
            Assert.DoesNotMatch("(?i)(?<!not a statement that the recipe is |not a guarantee that a dish is )free (of|from)", statement);

            // A profile named for what it leaves out is never the dish's description: no "Fits Nut-Free", no
            // "is Gluten-Free".
            Assert.DoesNotMatch($"(?i)^fits {profile}|\\bis {profile}", statement);
        });
    }

    // ---- The version reader, as this analysis uses it ----

    [Fact]
    public void A_refused_snapshot_read_another_workspaces_included_is_no_version()
    {
        var refused = OperationResult<RecipeSnapshotServiceModel>.Failure(
            new OperationError(RecipeErrorCodes.RecipeNotFound, "That recipe was not found.", new Dictionary<string, string[]>()));

        Assert.Null(NutritionRecipeVersionReader.FromResult(refused));
    }

    // ---- Helpers ----

    private static DietaryAllergenAnalysisServiceModel Analyze(
        IReadOnlyList<NutritionEstimateLineInput> lines,
        IReadOnlyList<AnalysisSubject>? allergens = null,
        IReadOnlyList<AnalysisSubject>? profiles = null,
        IngredientTraitEvidenceServiceModel? evidence = null) =>
        DietaryAllergenAnalyzer.Analyze(
            Guid.Parse("00000000-0000-0000-0000-0000000000e1"),
            Guid.Parse("00000000-0000-0000-0000-0000000000e2"),
            2,
            lines,
            allergens ?? [],
            profiles ?? [],
            evidence ?? Evidence(),
            Today);

    private static Guid LineId(int index) => new($"00000000-0000-0000-0002-{index:D12}");

    private static NutritionEstimateLineInput Line(int index, Guid? ingredient, bool optional = false, string? text = null) =>
        new(LineId(index), text ?? $"line {index}", null, null, null, ingredient, optional);

    private static object Allergen(Guid ingredient, AnalysisSubject allergen, AllergenPresence presence, TraitSourceServiceModel source, string note = "Stated.") =>
        new ApprovedAllergenTraitServiceModel(ingredient, allergen.Id, presence, note, new DateOnly(2026, 1, 1), source);

    private static object Dietary(Guid ingredient, AnalysisSubject profile, DietaryCompatibility state, TraitSourceServiceModel source, string note = "Stated.") =>
        new ApprovedDietaryTraitServiceModel(ingredient, profile.Id, state, note, new DateOnly(2026, 1, 1), source);

    private static IngredientTraitEvidenceServiceModel Evidence(params object[] traits) => new(
        [.. traits.OfType<ApprovedAllergenTraitServiceModel>()],
        [.. traits.OfType<ApprovedDietaryTraitServiceModel>()]);
}

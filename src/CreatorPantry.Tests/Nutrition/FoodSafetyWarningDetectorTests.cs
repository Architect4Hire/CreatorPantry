using CreatorPantry.Domain.Modules.Ingredients.Managers;
using CreatorPantry.Domain.Modules.Nutrition.Business;
using CreatorPantry.Domain.Modules.Nutrition.Managers;
using CreatorPantry.Domain.Modules.Vocabulary.Managers;

namespace CreatorPantry.Tests.Nutrition;

/// <summary>
/// The food-safety checks as a pure function (14.5, B-37): positive, negative and ambiguous wording for each
/// category, flagged techniques, what each warning is checked against, and the guard that nothing it says asserts
/// safety.
/// </summary>
public sealed class FoodSafetyWarningDetectorTests
{
    private static readonly Guid Beef = Guid.NewGuid();
    private static readonly Guid Salmon = Guid.NewGuid();
    private static readonly Guid Egg = Guid.NewGuid();
    private static readonly Guid Milk = Guid.NewGuid();
    private static readonly Guid Flour = Guid.NewGuid();

    private static readonly IReadOnlyDictionary<Guid, IngredientCategoryServiceModel> Ingredients =
        new[]
        {
            new IngredientCategoryServiceModel(Beef, "Beef", "meat-and-poultry"),
            new IngredientCategoryServiceModel(Salmon, "Salmon", "seafood"),
            new IngredientCategoryServiceModel(Egg, "Egg", "dairy-and-eggs"),
            new IngredientCategoryServiceModel(Milk, "Whole milk", "dairy-and-eggs"),
            new IngredientCategoryServiceModel(Flour, "All-purpose flour", "baking"),
        }.ToDictionary(ingredient => ingredient.IngredientId);

    private static readonly IReadOnlyDictionary<string, CookingTechniqueServiceModel> TechniquesByCode =
        new (string Code, string Name, bool Caution)[]
        {
            ("bake", "Bake", false),
            ("no-cook", "No-Cook", false),
            ("water-bath-canning", "Water-Bath Canning", true),
            ("pressure-canning", "Pressure Canning", true),
            ("ferment", "Ferment", true),
            ("cure", "Cure", true),
            ("smoke", "Smoke", true),
            ("sous-vide", "Sous-Vide", true),
            ("dehydrate", "Dehydrate", true),
            ("infuse-oil", "Infuse Oil", true),
            ("acidify", "Acidify", true),
        }.ToDictionary(entry => entry.Code, entry => new CookingTechniqueServiceModel(Guid.NewGuid(), entry.Code, entry.Name, entry.Caution));

    private static readonly IReadOnlyDictionary<Guid, CookingTechniqueServiceModel> Techniques =
        TechniquesByCode.Values.ToDictionary(technique => technique.Id);

    // ---- Positive: each category is detected from the wording that names it ----

    [Theory]
    [InlineData(FoodSafetyCategory.Canning, "Process the filled jars for 10 minutes.")]
    [InlineData(FoodSafetyCategory.Canning, "Leave 1/2 inch headspace and wipe the rims.")]
    [InlineData(FoodSafetyCategory.Canning, "Pressure-can at 10 lb for 75 minutes.")]
    [InlineData(FoodSafetyCategory.Fermentation, "Pack into a crock and let it ferment at room temperature for 2 weeks.")]
    [InlineData(FoodSafetyCategory.Fermentation, "Submerge the lacto-fermented carrots in brine.")]
    [InlineData(FoodSafetyCategory.CuringOrSmoking, "Dry-cure the pork belly for 7 days.")]
    [InlineData(FoodSafetyCategory.CuringOrSmoking, "Cold-smoke the salmon for 6 hours.")]
    [InlineData(FoodSafetyCategory.SousVide, "Cook sous vide at 57°C for 2 hours.")]
    [InlineData(FoodSafetyCategory.PregnancyOrMedicalDiet, "This soup is safe during pregnancy.")]
    [InlineData(FoodSafetyCategory.PregnancyOrMedicalDiet, "Suitable for babies from six months.")]
    [InlineData(FoodSafetyCategory.PregnancyOrMedicalDiet, "A ginger tea that cures colds.")]
    [InlineData(FoodSafetyCategory.PregnancyOrMedicalDiet, "Cinnamon lowers blood sugar.")]
    [InlineData(FoodSafetyCategory.UnsupportedSafetyClaim, "It is shelf-stable for a year.")]
    [InlineData(FoodSafetyCategory.UnsupportedSafetyClaim, "No need to refrigerate.")]
    [InlineData(FoodSafetyCategory.UnsupportedSafetyClaim, "The vinegar kills bacteria.")]
    [InlineData(FoodSafetyCategory.UnsupportedSafetyClaim, "Keeps for 6 months in the pantry.")]
    [InlineData(FoodSafetyCategory.UnsupportedSafetyClaim, "Perfectly safe to eat after a week on the counter.")]
    [InlineData(FoodSafetyCategory.Drying, "Dehydrate at 60°C for 8 hours.")]
    [InlineData(FoodSafetyCategory.OilInfusion, "Gently infuse the oil with the garlic over low heat, then bottle it.")]
    [InlineData(FoodSafetyCategory.OilInfusion, "Make a garlic confit and keep it in the fridge.")]
    public void A_step_naming_the_method_or_making_the_claim_is_detected(FoodSafetyCategory category, string step)
    {
        var warning = Single(Detect(steps: [step]), category);

        Assert.Equal(FoodSafetyConfidence.Detected, warning.Confidence);
        var trigger = Assert.Single(warning.Triggers);
        Assert.Equal((FoodSafetyField.Step, FoodSafetyTriggerKind.Text), (trigger.Field, trigger.Kind));
    }

    [Theory]
    [InlineData("1 tsp Prague powder #1")]
    [InlineData("2 g curing salt")]
    [InlineData("1 tsp pink curing salt")]
    public void A_curing_agent_in_an_ingredient_line_is_detected(string line)
    {
        var warning = Single(Detect(lines: [(line, null)]), FoodSafetyCategory.CuringOrSmoking);

        Assert.Equal(FoodSafetyConfidence.Detected, warning.Confidence);
        Assert.Equal(FoodSafetyField.IngredientLine, Assert.Single(warning.Triggers).Field);
    }

    [Theory]
    [InlineData(FoodSafetyCategory.SousVide, "Immersion circulator")]
    [InlineData(FoodSafetyCategory.Canning, "Pressure canner")]
    [InlineData(FoodSafetyCategory.Drying, "Dehydrator")]
    [InlineData(FoodSafetyCategory.CuringOrSmoking, "Smoker")]
    [InlineData(FoodSafetyCategory.Fermentation, "Fermentation crock")]
    public void Equipment_that_only_one_method_uses_is_detected(FoodSafetyCategory category, string equipment)
    {
        Assert.Equal(FoodSafetyConfidence.Detected, Single(Detect(equipment: [equipment]), category).Confidence);
    }

    [Fact]
    public void A_title_naming_a_cured_dish_is_detected()
    {
        Assert.Equal(FoodSafetyField.Title, Assert.Single(Single(Detect(title: "Beetroot gravlax"), FoodSafetyCategory.CuringOrSmoking).Triggers).Field);
    }

    // ---- Raw and undercooked protein ----

    [Fact]
    public void A_raw_dish_with_a_protein_ingredient_is_detected_and_names_the_line()
    {
        var warning = Single(Detect(title: "Classic steak tartare", lines: [("200 g beef fillet", Beef)]), FoodSafetyCategory.RawOrUndercookedProtein);

        Assert.Equal(FoodSafetyConfidence.Detected, warning.Confidence);
        Assert.Contains(warning.Triggers, trigger => trigger is { Field: FoodSafetyField.Title, Kind: FoodSafetyTriggerKind.Text });
        Assert.Contains(warning.Triggers, trigger => trigger is { Field: FoodSafetyField.IngredientLine, Kind: FoodSafetyTriggerKind.IngredientCategory, Matched: "200 g beef fillet" });
    }

    [Theory]
    [InlineData("Top each bowl with a raw egg yolk.")]
    [InlineData("Serve with a runny yolk.")]
    public void Raw_or_runny_eggs_with_an_egg_ingredient_are_detected(string step)
    {
        Assert.Equal(
            FoodSafetyConfidence.Detected,
            Single(Detect(steps: [step], lines: [("2 eggs", Egg)]), FoodSafetyCategory.RawOrUndercookedProtein).Confidence);
    }

    [Fact]
    public void Served_raw_counts_only_when_the_sentence_is_about_a_protein()
    {
        Assert.Equal(
            FoodSafetyConfidence.Detected,
            Single(Detect(steps: ["The scallops are served raw with lime."], lines: [("200 g salmon", Salmon)]), FoodSafetyCategory.RawOrUndercookedProtein).Confidence);

        Assert.Empty(Detect(steps: ["Thinly slice the radishes; they are served raw on the side."], lines: [("200 g salmon", Salmon)]).Warnings);
    }

    [Fact]
    public void No_cook_with_eggs_is_detected_and_no_cook_alone_is_nothing()
    {
        var withEggs = Single(
            Detect(primaryTechnique: TechniquesByCode["no-cook"].Id, lines: [("3 eggs", Egg)]), FoodSafetyCategory.RawOrUndercookedProtein);

        Assert.Equal(FoodSafetyConfidence.Detected, withEggs.Confidence);
        Assert.Contains(withEggs.Triggers, trigger => trigger is { Kind: FoodSafetyTriggerKind.Technique, Matched: "no-cook" });

        Assert.Empty(Detect(primaryTechnique: TechniquesByCode["no-cook"].Id, lines: [("2 cups milk", Milk), ("1 cup flour", Flour)]).Warnings);
    }

    [Fact]
    public void A_raw_dish_whose_lines_name_no_protein_is_only_possible()
    {
        Assert.Equal(
            FoodSafetyConfidence.Possible,
            Single(Detect(title: "Classic steak tartare", lines: [("1 shallot", null)]), FoodSafetyCategory.RawOrUndercookedProtein).Confidence);
    }

    [Theory]
    [InlineData("Sear the steak to medium-rare.")]
    [InlineData("Grill them rare and slice thinly.")]
    [InlineData("Halve the soft-boiled eggs.")]
    public void Rare_meat_and_soft_eggs_are_possible(string step)
    {
        Assert.Equal(
            FoodSafetyConfidence.Possible,
            Single(Detect(steps: [step], lines: [("1 steak", Beef)]), FoodSafetyCategory.RawOrUndercookedProtein).Confidence);
    }

    [Fact]
    public void Sushi_grade_fish_in_a_line_is_possible()
    {
        Assert.Equal(
            FoodSafetyConfidence.Possible,
            Single(Detect(lines: [("200 g sushi-grade tuna", Salmon)]), FoodSafetyCategory.RawOrUndercookedProtein).Confidence);
    }

    // ---- Internal temperature ----

    [Fact]
    public void A_stated_internal_temperature_is_detected_and_recorded_as_written()
    {
        var warning = Single(
            Detect(steps: ["Roast until an instant-read thermometer in the thickest part reads 165°F."], lines: [("1 chicken", Beef)]),
            FoodSafetyCategory.InternalTemperature);

        Assert.Equal(FoodSafetyConfidence.Detected, warning.Confidence);
        var stated = Assert.Single(warning.StatedTemperatures);
        Assert.Equal((165m, FoodSafetyTemperatureScale.Fahrenheit, FoodSafetyField.Step), (stated.Value, stated.Scale, stated.Field));
    }

    [Fact]
    public void An_internal_temperature_in_celsius_is_recorded_in_celsius()
    {
        var stated = Assert.Single(Single(
            Detect(steps: ["Cook to an internal temperature of 74 °C."], lines: [("4 chicken thighs", Beef)]),
            FoodSafetyCategory.InternalTemperature).StatedTemperatures);

        Assert.Equal((74m, FoodSafetyTemperatureScale.Celsius), (stated.Value, stated.Scale));
    }

    [Fact]
    public void A_thermometer_doneness_test_with_no_figure_is_possible()
    {
        var warning = Single(
            Detect(steps: ["Roast until an instant-read thermometer says it is done."], lines: [("1 chicken", Beef)]),
            FoodSafetyCategory.InternalTemperature);

        Assert.Equal(FoodSafetyConfidence.Possible, warning.Confidence);
        Assert.Empty(warning.StatedTemperatures);
    }

    // ---- Ambiguous: possible, with a message saying what it might be ----

    [Theory]
    [InlineData(FoodSafetyCategory.Canning, "Seal the jars and store in the pantry.")]
    [InlineData(FoodSafetyCategory.Fermentation, "Leave the jar at room temperature for 3 days.")]
    [InlineData(FoodSafetyCategory.SousVide, "Hold the bagged steaks in a 55°C water bath for 2 hours.")]
    [InlineData(FoodSafetyCategory.PregnancyOrMedicalDiet, "I craved this all through my pregnancy.")]
    [InlineData(FoodSafetyCategory.UnsupportedSafetyClaim, "Keeps for 2 weeks.")]
    [InlineData(FoodSafetyCategory.OilInfusion, "Finish with a drizzle of garlic-infused oil.")]
    public void Ambiguous_wording_is_possible(FoodSafetyCategory category, string step)
    {
        var warning = Single(Detect(steps: [step]), category);

        Assert.Equal(FoodSafetyConfidence.Possible, warning.Confidence);
        Assert.Equal(FoodSafetyWarningPolicy.Message(category, FoodSafetyConfidence.Possible), warning.Message);
    }

    [Fact]
    public void Bare_pink_salt_in_a_line_is_possible()
    {
        Assert.Equal(FoodSafetyConfidence.Possible, Single(Detect(lines: [("1 tsp pink salt", null)]), FoodSafetyCategory.CuringOrSmoking).Confidence);
    }

    // ---- Negative: ordinary recipes raise nothing ----

    [Theory]
    [InlineData("Add the canned tomatoes and simmer.")]
    [InlineData("You can make this ahead.")]
    [InlineData("Stir in the fermented black beans.")]
    [InlineData("Let the dough bulk ferment for 4 hours.")]
    [InlineData("Feed the sourdough starter and let it ferment overnight.")]
    [InlineData("Bake the cheesecake in a water bath at 160°C for 1 hour.")]
    [InlineData("Add a handful of baby spinach.")]
    [InlineData("Use oven mitts to safely remove the pan.")]
    [InlineData("Top with raw red onion.")]
    [InlineData("Pour into a mason jar and shake to combine.")]
    [InlineData("Quick-pickle the onions in vinegar for 20 minutes.")]
    [InlineData("Season the cast iron skillet to cure it.")]
    [InlineData("Salt kills yeast, so keep them apart.")]
    [InlineData("There is no risk of curdling if you temper the eggs.")]
    [InlineData("Drizzle with chilli oil.")]
    [InlineData("Heat the oil until it starts to smoke.")]
    [InlineData("Infuse the cream with rosemary, then whisk in a little olive oil to finish.")]
    public void An_ordinary_step_raises_nothing(string step)
    {
        Assert.Empty(Detect(steps: [step]).Warnings);
    }

    [Theory]
    [InlineData("1 can (400 g) canned tomatoes")]
    [InlineData("2 tbsp kimchi")]
    [InlineData("1 tbsp white miso")]
    [InlineData("4 slices prosciutto")]
    [InlineData("1 tsp pink Himalayan salt")]
    [InlineData("2 tbsp raw honey")]
    [InlineData("1/2 cup raw cashews")]
    [InlineData("1 tbsp kosher salt")]
    [InlineData("2 tbsp garlic-infused oil")]
    [InlineData("1 tsp smoked paprika")]
    public void A_bought_ingredient_raises_nothing(string line)
    {
        Assert.Empty(Detect(lines: [(line, null)]).Warnings);
    }

    [Theory]
    [InlineData("Vegan brownies")]
    [InlineData("Baby back ribs")]
    [InlineData("Kimchi fried rice")]
    public void A_dish_name_raises_nothing(string title)
    {
        Assert.Empty(Detect(title: title).Warnings);
    }

    [Fact]
    public void Raw_protein_going_into_the_pan_raises_nothing()
    {
        Assert.Empty(Detect(steps: ["Add the raw shrimp and cook until pink."], lines: [("400 g shrimp", Salmon)]).Warnings);
    }

    [Theory]
    [InlineData("Preheat the oven to 200°C, then roast for 40 minutes.")]
    [InlineData("Heat the oil to 180°C, checking with a thermometer, and fry the chicken.")]
    public void An_oven_or_frying_temperature_is_not_an_internal_temperature(string step)
    {
        Assert.Empty(Detect(steps: [step], lines: [("1 chicken", Beef)]).Warnings);
    }

    [Fact]
    public void A_bread_doneness_temperature_in_a_recipe_with_no_protein_raises_nothing()
    {
        Assert.Empty(Detect(steps: ["Bake until the internal temperature reaches 95°C."], lines: [("500 g flour", Flour)]).Warnings);
    }

    [Fact]
    public void A_keeping_time_with_its_storage_condition_raises_nothing()
    {
        Assert.Empty(Detect(storage: "Store covered. Keeps 3 days.").Warnings);
        Assert.Empty(Detect(storage: "Keeps 3 days in the fridge.").Warnings);
    }

    [Fact]
    public void An_ordinary_recipe_raises_nothing_and_still_carries_the_disclaimer()
    {
        var result = Detect(
            title: "Roast vegetables with chocolate cake to follow",
            headnote: "A family favourite for Sunday lunch.",
            lines: [("500 g carrots", null), ("200 g flour", Flour), ("2 eggs", Egg), ("250 ml milk", Milk)],
            steps:
            [
                "Preheat the oven to 200°C.",
                "Roast the carrots for 40 minutes until caramelised.",
                "Whisk the eggs and milk into the flour and bake at 180°C for 30 minutes.",
                "Cool on a rack for 10 minutes before slicing.",
            ],
            stepTechniques: [null, TechniquesByCode["bake"].Id, TechniquesByCode["bake"].Id, null],
            storage: "Keeps 3 days in an airtight container.");

        Assert.Empty(result.Warnings);
        Assert.Equal(FoodSafetyWarningPolicy.Disclaimer, result.Disclaimer);
        Assert.Equal(FoodSafetyWarningPolicy.MethodVersion, result.MethodVersion);
    }

    // ---- Structured techniques ----

    [Theory]
    [InlineData("water-bath-canning", FoodSafetyCategory.Canning)]
    [InlineData("pressure-canning", FoodSafetyCategory.Canning)]
    [InlineData("ferment", FoodSafetyCategory.Fermentation)]
    [InlineData("cure", FoodSafetyCategory.CuringOrSmoking)]
    [InlineData("smoke", FoodSafetyCategory.CuringOrSmoking)]
    [InlineData("sous-vide", FoodSafetyCategory.SousVide)]
    [InlineData("dehydrate", FoodSafetyCategory.Drying)]
    [InlineData("infuse-oil", FoodSafetyCategory.OilInfusion)]
    public void Every_flagged_technique_is_detected_on_its_own(string code, FoodSafetyCategory category)
    {
        var warning = Assert.Single(Detect(steps: ["Carry on."], stepTechniques: [TechniquesByCode[code].Id]).Warnings);

        Assert.Equal((category, FoodSafetyConfidence.Detected), (warning.Category, warning.Confidence));
        Assert.Equal(new FoodSafetyTriggerServiceModel(FoodSafetyField.StepTechnique, warning.Triggers[0].ItemId, FoodSafetyTriggerKind.Technique, code), Assert.Single(warning.Triggers));
    }

    [Fact]
    public void A_flagged_technique_this_list_does_not_name_is_reported_rather_than_dropped()
    {
        var warning = Assert.Single(Detect(primaryTechnique: TechniquesByCode["acidify"].Id).Warnings);

        Assert.Equal(FoodSafetyCategory.OtherFlaggedMethod, warning.Category);
        Assert.Contains("Acidify", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unflagged_technique_raises_nothing()
    {
        var result = Detect(primaryTechnique: TechniquesByCode["bake"].Id);

        Assert.Empty(result.Warnings);
        Assert.Equal(0, result.UnrecognisedTechniques);
    }

    [Fact]
    public void A_retired_technique_raises_nothing_but_is_counted_so_an_empty_list_cannot_hide_it()
    {
        var result = Detect(primaryTechnique: Guid.NewGuid(), steps: ["Carry on.", "Carry on."], stepTechniques: [Guid.NewGuid(), TechniquesByCode["bake"].Id]);

        Assert.Empty(result.Warnings);
        Assert.Equal(2, result.UnrecognisedTechniques);
    }

    [Fact]
    public void A_technique_and_the_text_for_the_same_method_are_one_warning_citing_both()
    {
        var result = Detect(steps: ["Cook sous vide at 57°C for 2 hours."], stepTechniques: [TechniquesByCode["sous-vide"].Id]);

        var warning = Assert.Single(result.Warnings);
        Assert.Equal(FoodSafetyCategory.SousVide, warning.Category);
        Assert.Equal([FoodSafetyTriggerKind.Technique, FoodSafetyTriggerKind.Text], warning.Triggers.Select(trigger => trigger.Kind));
    }

    [Fact]
    public void A_possible_and_a_detected_trigger_for_one_category_make_a_detected_warning()
    {
        var warning = Single(
            Detect(steps: ["Seal the jars and store in the pantry.", "Leave 1/2 inch headspace."]),
            FoodSafetyCategory.Canning);

        Assert.Equal(FoodSafetyConfidence.Detected, warning.Confidence);
        Assert.True(warning.Triggers.Count >= 2);
    }

    [Fact]
    public void Warnings_come_in_category_order()
    {
        var result = Detect(steps: ["Dehydrate at 60°C for 8 hours.", "Cook sous vide at 57°C for 2 hours.", "Leave 1/2 inch headspace."]);

        Assert.Equal([FoodSafetyCategory.Canning, FoodSafetyCategory.SousVide, FoodSafetyCategory.Drying], result.Warnings.Select(warning => warning.Category));
    }

    // ---- Internal temperature: figures as written, the cue's own figures, real statements kept ----

    [Fact]
    public void A_scale_after_a_space_is_read()
    {
        var stated = Assert.Single(Single(
            Detect(steps: ["Insert an instant-read thermometer in the thickest part; it should register 165 F."], lines: [("1 chicken", Beef)]),
            FoodSafetyCategory.InternalTemperature).StatedTemperatures);

        Assert.Equal((165m, FoodSafetyTemperatureScale.Fahrenheit), (stated.Value, stated.Scale));
    }

    [Fact]
    public void Both_ends_of_a_range_are_recorded()
    {
        var warning = Single(Detect(steps: ["Cook the pork to an internal temperature of 145-160°F."]), FoodSafetyCategory.InternalTemperature);

        Assert.Equal([145m, 160m], warning.StatedTemperatures.Select(stated => stated.Value));
        Assert.All(warning.StatedTemperatures, stated => Assert.Equal(FoodSafetyTemperatureScale.Fahrenheit, stated.Scale));
    }

    [Fact]
    public void An_oven_temperature_before_the_cue_is_not_recorded_as_internal()
    {
        var stated = Assert.Single(Single(
            Detect(steps: ["Roast at 425°F until the thickest part reads 165°F."], lines: [("1 chicken", Beef)]),
            FoodSafetyCategory.InternalTemperature).StatedTemperatures);

        Assert.Equal(165m, stated.Value);
    }

    [Fact]
    public void A_figure_in_the_sentence_beside_the_cue_is_read_and_not_called_missing()
    {
        var warning = Single(
            Detect(steps: ["Cook the chicken to 165°F. Check with a thermometer in the thickest part."]),
            FoodSafetyCategory.InternalTemperature);

        Assert.Equal(FoodSafetyConfidence.Detected, warning.Confidence);
        Assert.Equal(165m, Assert.Single(warning.StatedTemperatures).Value);
    }

    [Theory]
    [InlineData("Preheat the oven to 400°F and roast the chicken until the internal temperature reaches 165°F.", 165)]
    [InlineData("Pan-fry the pork chops until the internal temp hits 145°F.", 145)]
    [InlineData("Braise the pork in milk until the thickest part reads 190°F.", 190)]
    [InlineData("Roast the turkey with bread stuffing until the thigh reads 175°F.", 175)]
    [InlineData("Grill the burgers until the internal temp reaches 160°F.", 160)]
    public void A_real_internal_temperature_is_detected_whatever_else_the_sentence_mentions(string step, int figure)
    {
        // No ingredient lines at all: the sentence names its own protein.
        var warning = Single(Detect(steps: [step]), FoodSafetyCategory.InternalTemperature);

        Assert.Equal(FoodSafetyConfidence.Detected, warning.Confidence);
        Assert.Equal((decimal)figure, Assert.Single(warning.StatedTemperatures).Value);
    }

    [Theory]
    [InlineData("Bake the cake until a thermometer inserted in the centre reads 205°F.")]
    [InlineData("Bake until a probe thermometer reads 205°F.")]
    public void A_bake_with_only_eggs_for_protein_is_not_an_internal_temperature(string step)
    {
        Assert.Empty(Detect(steps: [step], lines: [("3 eggs", Egg), ("200 g flour", Flour)]).Warnings);
    }

    // ---- Raw protein: the cases the first list missed, and the ones it misread ----

    [Theory]
    [InlineData("Fold the whites into the chocolate mousse.")]
    [InlineData("Make the hollandaise while the eggs poach.")]
    [InlineData("Whisk the yolks and oil to make the mayonnaise.")]
    [InlineData("Layer the tiramisu and chill.")]
    [InlineData("Toss the hot pasta through the carbonara sauce.")]
    public void A_dish_often_made_with_raw_egg_is_possible_where_the_recipe_has_eggs(string step)
    {
        Assert.Equal(
            FoodSafetyConfidence.Possible,
            Single(Detect(steps: [step], lines: [("3 eggs", Egg)]), FoodSafetyCategory.RawOrUndercookedProtein).Confidence);
    }

    [Theory]
    [InlineData("Spoon over the hollandaise.", false)]
    [InlineData("Spread the mayonnaise on the bread.", true)]
    public void A_bought_or_eggless_sauce_raises_nothing(string step, bool withEggs)
    {
        Assert.Empty(Detect(steps: [step], lines: withEggs ? [("2 eggs", Egg)] : []).Warnings);
    }

    [Fact]
    public void A_pink_centre_is_possible_and_no_longer_pink_is_nothing()
    {
        Assert.Equal(
            FoodSafetyConfidence.Possible,
            Single(Detect(steps: ["Sear the tuna 30 seconds a side; the centre should stay pink."]), FoodSafetyCategory.RawOrUndercookedProtein).Confidence);

        Assert.Empty(Detect(steps: ["Cook until no longer pink in the middle."], lines: [("4 chicken thighs", Beef)]).Warnings);
    }

    [Theory]
    [InlineData("Arrange the beet carpaccio on a plate.")]
    [InlineData("Mix 1 raw egg into the meat mixture, then bake.")]
    [InlineData("Brush with raw egg and bake until golden.")]
    [InlineData("Never serve raw chicken.")]
    [InlineData("Do not eat the raw egg.")]
    public void A_vegetable_dish_a_cooked_egg_or_a_caution_is_not_raw_service(string step)
    {
        Assert.Empty(Detect(steps: [step], lines: [("200 g beef", Beef), ("1 egg", Egg)]).Warnings);
    }

    [Fact]
    public void A_poke_bowl_is_possible_even_beside_a_protein()
    {
        Assert.Equal(
            FoodSafetyConfidence.Possible,
            Single(Detect(steps: ["Spoon into poke bowls."], lines: [("200 g salmon", Salmon)]), FoodSafetyCategory.RawOrUndercookedProtein).Confidence);
    }

    // ---- Claims, canning, fermentation, drying, oil, curing and medical: ordinary wording raises nothing ----

    [Theory]
    [InlineData("No need to refrigerate the cookie dough before baking.")]
    [InlineData("The bread does not need to be refrigerated.")]
    [InlineData("Discard it; it is no longer safe to eat.")]
    [InlineData("Use shelf-stable tofu.")]
    [InlineData("Leave 1 inch headspace in the container before freezing.")]
    [InlineData("Pour into the blender jar and process for 30 seconds.")]
    [InlineData("Miso, fermented for two years, gives this its depth.")]
    [InlineData("Air-dry the cookies until the icing sets.")]
    [InlineData("Cover to keep the chicken from dehydrating.")]
    [InlineData("Sprinkle with beef jerky seasoning.")]
    [InlineData("Infuse the hot oil with garlic and chili flakes for 30 seconds.")]
    [InlineData("Toddler-approved and ready in 20 minutes.")]
    [InlineData("A gut-friendly breakfast.")]
    [InlineData("Melt the chocolate in a water bath at 50°C for 1 hour.")]
    public void Ordinary_wording_the_review_traced_raises_nothing(string step)
    {
        Assert.Empty(Detect(steps: [step]).Warnings);
    }

    [Theory]
    [InlineData("8 slices bacon (no sodium nitrite added)")]
    [InlineData("4 sausages, sodium nitrite-free")]
    public void A_label_saying_a_curing_agent_is_absent_raises_nothing(string line)
    {
        Assert.Empty(Detect(lines: [(line, null)]).Warnings);
    }

    [Theory]
    [InlineData("This jam is shelf-stable for a year.")]
    [InlineData("Totally allergy-safe.")]
    [InlineData("Safe for people with nut allergies.")]
    public void A_shelf_life_or_allergy_assurance_is_a_detected_claim(string step)
    {
        Assert.Equal(FoodSafetyConfidence.Detected, Single(Detect(steps: [step]), FoodSafetyCategory.UnsupportedSafetyClaim).Confidence);
    }

    [Fact]
    public void A_keeping_time_is_not_flagged_when_another_field_says_how_the_food_is_stored()
    {
        Assert.Empty(Detect(steps: ["Store in an airtight container."], notes: "Keeps for 2 weeks.").Warnings);
    }

    // ---- Text is read in canonical form ----

    [Fact]
    public void Compatibility_characters_and_invisible_ones_do_not_hide_a_method()
    {
        Assert.Single(Detect(steps: ["Cook sous​vide for 2 hours."]).Warnings);
        Assert.Single(Detect(steps: ["Cold‑smoke the trout."]).Warnings);

        var stated = Assert.Single(Single(
            Detect(steps: ["Cook until the internal temperature is ７４℃."], lines: [("1 chicken", Beef)]),
            FoodSafetyCategory.InternalTemperature).StatedTemperatures);
        Assert.Equal((74m, FoodSafetyTemperatureScale.Celsius), (stated.Value, stated.Scale));
    }

    [Fact]
    public void A_quoted_trigger_is_held_to_its_maximum_length()
    {
        var sentence = "Seal the jars " + string.Concat(Enumerable.Repeat("very carefully and ", 12)) + "store them in the pantry.";

        var trigger = Assert.Single(Single(Detect(steps: [sentence]), FoodSafetyCategory.Canning).Triggers);

        Assert.Equal(FoodSafetyWarningPolicy.MaxMatchedLength, trigger.Matched.Length);
        Assert.EndsWith("…", trigger.Matched, StringComparison.Ordinal);
    }

    // ---- What each warning is checked against ----

    [Fact]
    public void With_no_approved_source_every_warning_says_verification_is_required()
    {
        var result = Detect(steps: ["Leave 1/2 inch headspace.", "Cook sous vide at 57°C for 2 hours."]);

        Assert.All(result.Warnings, warning =>
        {
            Assert.Equal(FoodSafetyEvidenceStatus.NoApprovedSource, warning.Evidence.Status);
            Assert.True(warning.Evidence.VerificationRequired);
            Assert.Null(warning.Evidence.Citation);
            Assert.StartsWith("Verification required:", warning.Evidence.Statement, StringComparison.Ordinal);
            Assert.Contains("no approved food-safety reference", warning.Evidence.Statement, StringComparison.Ordinal);
        });
    }

    [Theory]
    [InlineData(FoodSafetyEvidenceStatus.NotConfigured, "not available on this installation")]
    [InlineData(FoodSafetyEvidenceStatus.Unavailable, "could not be read")]
    [InlineData(FoodSafetyEvidenceStatus.NoReference, "has nothing on this")]
    public void A_source_that_cannot_answer_still_says_verification_is_required(FoodSafetyEvidenceStatus status, string says)
    {
        var evidence = Assert.Single(Detect(steps: ["Leave 1/2 inch headspace."], evidence: FoodSafetyEvidenceInput.Uncited(status)).Warnings).Evidence;

        Assert.Equal(status, evidence.Status);
        Assert.True(evidence.VerificationRequired);
        Assert.Null(evidence.Citation);
        Assert.StartsWith("Verification required:", evidence.Statement, StringComparison.Ordinal);
        Assert.Contains(says, evidence.Statement, StringComparison.Ordinal);
    }

    [Fact]
    public void An_approved_reference_is_cited_only_for_the_category_it_covers()
    {
        var citation = new FoodSafetyCitationServiceModel(
            "test-canning-guide", "A test canning guide", "2037-01", "Test canning guide, chapter 2.", "CC0-1.0", "https://example.test/guide",
            new DateTimeOffset(2037, 10, 20, 9, 0, 0, TimeSpan.Zero));
        var evidence = new FoodSafetyEvidenceInput(
            FoodSafetyEvidenceStatus.NoReference,
            new Dictionary<FoodSafetyCategory, FoodSafetyCitationServiceModel> { [FoodSafetyCategory.Canning] = citation });

        var result = Detect(steps: ["Leave 1/2 inch headspace.", "Cook sous vide at 57°C for 2 hours."], evidence: evidence);

        var canning = Single(result, FoodSafetyCategory.Canning).Evidence;
        Assert.Equal((FoodSafetyEvidenceStatus.Cited, false, citation), (canning.Status, canning.VerificationRequired, canning.Citation));
        Assert.Equal("Check this against A test canning guide: Test canning guide, chapter 2. Version 2037-01, retrieved 2037-10-20.", canning.Statement);

        Assert.Equal(FoodSafetyEvidenceStatus.NoReference, Single(result, FoodSafetyCategory.SousVide).Evidence.Status);
    }

    [Fact]
    public void A_category_without_a_citation_can_never_be_reported_as_cited()
    {
        Assert.Throws<ArgumentException>(() => Detect(evidence: FoodSafetyEvidenceInput.Uncited(FoodSafetyEvidenceStatus.Cited)));
    }

    [Theory]
    [InlineData(ReferenceEvidenceAvailability.NoApprovedSource, FoodSafetyEvidenceStatus.NoApprovedSource)]
    [InlineData(ReferenceEvidenceAvailability.NotConfigured, FoodSafetyEvidenceStatus.NotConfigured)]
    // Nothing reads a food-safety reference yet, so a reachable source still has no citation to give.
    [InlineData(ReferenceEvidenceAvailability.Available, FoodSafetyEvidenceStatus.Unavailable)]
    public void Coverage_maps_to_a_reason_for_verification_and_never_to_cited(ReferenceEvidenceAvailability availability, FoodSafetyEvidenceStatus expected)
    {
        var evidence = FoodSafetyWarningBusiness.Evidence(new ReferenceEvidenceCoverageServiceModel(ReferenceEvidenceKind.FoodSafety, availability, "a-source"));

        Assert.Equal(expected, evidence.WhenNotCited);
        Assert.Empty(evidence.Citations);
    }

    // ---- Nothing here can say "safe" ----

    [Fact]
    public void No_message_or_statement_asserts_safety()
    {
        var sentences = Enum.GetValues<FoodSafetyCategory>()
            .SelectMany(category => Enum.GetValues<FoodSafetyConfidence>().Select(confidence => FoodSafetyWarningPolicy.Message(category, confidence, "Acidify")))
            .Concat(Enum.GetValues<FoodSafetyEvidenceStatus>().Select(status => FoodSafetyWarningPolicy.Statement(status)))
            .ToList();

        Assert.All(sentences, sentence => Assert.DoesNotMatch(@"(?i)\bsafe\b|\bunsafe\b|guarantee|certif|risk-free|\bapproved for\b", sentence));
    }

    [Fact]
    public void The_disclaimer_denies_both_a_clean_bill_and_a_ruling()
    {
        Assert.Contains("not a statement that the recipe is safe", FoodSafetyWarningPolicy.Disclaimer, StringComparison.Ordinal);
        Assert.Contains("not a ruling that it is unsafe", FoodSafetyWarningPolicy.Disclaimer, StringComparison.Ordinal);
    }

    // ---- Helpers ----

    private static FoodSafetyWarningServiceModel Single(FoodSafetyWarningsServiceModel result, FoodSafetyCategory category) =>
        Assert.Single(result.Warnings, warning => warning.Category == category);

    private static FoodSafetyWarningsServiceModel Detect(
        string title = "Weeknight dinner",
        string? headnote = null,
        string? notes = null,
        string? storage = null,
        Guid? primaryTechnique = null,
        IReadOnlyList<(string Text, Guid? IngredientId)>? lines = null,
        IReadOnlyList<string>? steps = null,
        IReadOnlyList<Guid?>? stepTechniques = null,
        IReadOnlyList<string>? equipment = null,
        FoodSafetyEvidenceInput? evidence = null)
    {
        var stepTexts = steps ?? (stepTechniques is null ? [] : [.. stepTechniques.Select(_ => "Carry on.")]);

        var recipe = new FoodSafetyRecipeInput(
            1,
            title,
            null,
            headnote,
            notes,
            storage,
            primaryTechnique,
            [.. (lines ?? []).Select(line => new FoodSafetyLineInput(Guid.NewGuid(), line.Text, null, line.IngredientId))],
            [.. stepTexts.Select((text, i) => new FoodSafetyStepInput(Guid.NewGuid(), text, null, stepTechniques is { } chosen && i < chosen.Count ? chosen[i] : null))],
            [.. (equipment ?? []).Select(item => new FoodSafetyEquipmentInput(Guid.NewGuid(), item, null))]);

        return FoodSafetyWarningDetector.Detect(
            Guid.NewGuid(), Guid.NewGuid(), recipe, Techniques, Ingredients,
            evidence ?? FoodSafetyEvidenceInput.Uncited(FoodSafetyEvidenceStatus.NoApprovedSource));
    }
}

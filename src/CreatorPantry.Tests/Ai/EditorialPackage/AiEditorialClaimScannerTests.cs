using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.Recipes.Managers;

namespace CreatorPantry.Tests.Ai.EditorialPackage;

/// <summary>
/// The deterministic net under generated editorial prose (RCPUB-001): numbers, safety claims, storage guidance
/// and provenance the recipe does not support. Findings are warnings the model cannot suppress.
/// </summary>
public sealed class AiEditorialClaimScannerTests
{
    private static readonly AiEditorialSourceFacts Source = AiEditorialSourceFacts.Of(
        numbers: ["220", "25", "12", "1.5"],
        prose: "Mix the dough. Bake at 220C for 25 minutes. Makes 12 rolls. Gluten-free flour works too.",
        storageNotes: "Keeps two days in a tin.");

    private static AiEditorialSections Headnote(string text) => new() { Headnote = new AiEditorialText { Text = text } };

    private static IReadOnlyList<AiEditorialFinding> Scan(AiEditorialSections sections, AiEditorialSourceFacts? source = null) =>
        AiEditorialClaimScanner.Scan(sections, source ?? Source);

    // ---- numbers ----

    [Fact]
    public void A_number_the_recipe_states_is_not_reported()
    {
        Assert.Empty(Scan(Headnote("Bake at 220 degrees for 25 minutes and get 12 rolls.")));
    }

    [Theory]
    [InlineData("Bake for 45 minutes.")]
    [InlineData("Bake at 180 degrees.")]
    [InlineData("Serves 8.")]
    public void A_number_the_recipe_never_states_is_reported(string text)
    {
        var finding = Assert.Single(Scan(Headnote(text)));

        Assert.Equal(AiEditorialClaimScanner.UnsupportedNumber, finding.Code);
        Assert.Equal(AiWarningKind.UnverifiedClaim, finding.Kind);
        Assert.Equal(AiEditorialSection.Headnote, finding.Section);
    }

    [Fact]
    public void A_number_written_as_a_word_beside_a_unit_is_found_but_prose_is_not()
    {
        Assert.Single(Scan(Headnote("Rest it for two hours.")));
        Assert.Empty(Scan(Headnote("One of the best rolls you will make.")));
    }

    [Fact]
    public void A_time_in_hours_is_supported_by_the_same_time_in_minutes()
    {
        // From a snapshot, because that is where the one allowed derivation lives: a stated time also supports
        // the same time written in hours.
        var snapshot = new RecipeSnapshotDocument
        {
            SchemaVersion = RecipeSnapshotDocument.CurrentSchemaVersion,
            Recipe = new RecipeSnapshotHeader { Title = "Stew", CookTimeMinutes = 90 },
        };

        Assert.Empty(AiEditorialClaimScanner.Scan(Headnote("Takes 1.5 hours."), AiEditorialSourceFacts.From(snapshot)));
        Assert.Empty(AiEditorialClaimScanner.Scan(Headnote("Takes 90 minutes."), AiEditorialSourceFacts.From(snapshot)));
        Assert.Single(AiEditorialClaimScanner.Scan(Headnote("Takes 2 hours."), AiEditorialSourceFacts.From(snapshot)));
    }

    [Fact]
    public void A_total_the_model_worked_out_is_reported_because_arithmetic_belongs_to_the_domain()
    {
        // 25 + 12 is not a figure the recipe states.
        Assert.Single(Scan(Headnote("That is 37 minutes in all.")));
    }

    [Fact]
    public void Unit_suffixes_and_thousand_separators_are_read_as_the_number_they_carry()
    {
        var facts = AiEditorialSourceFacts.Of(["2000 g"], "Bake at 220C.", null);

        Assert.Empty(AiEditorialClaimScanner.Scan(Headnote("Bake at 220 degrees and use 2,000 g."), facts));
    }

    [Fact]
    public void One_unsupported_number_is_reported_once_per_text_however_often_it_repeats()
    {
        Assert.Single(Scan(Headnote("Bake 45 minutes, then 45 minutes more.")));
    }

    // ---- safety ----

    [Theory]
    [InlineData("This is a dairy-free treat.")]
    [InlineData("A perfectly safe to eat snack.")]
    [InlineData("Healthy and nutritious.")]
    [InlineData("Results are guaranteed.")]
    public void A_safety_allergen_dietary_or_health_claim_is_reported(string text)
    {
        var findings = Scan(Headnote(text)).Where(f => f.Code == AiEditorialClaimScanner.UnsupportedSafetyClaim).ToList();

        Assert.NotEmpty(findings);
        Assert.All(findings, finding => Assert.Equal(AiWarningKind.SafetyCaution, finding.Kind));
    }

    [Fact]
    public void A_claim_the_creator_wrote_themselves_is_not_reported()
    {
        // The recipe's own text says gluten-free; repeating the creator's claim is not the model's.
        Assert.Empty(Scan(Headnote("Made with gluten-free flour.")));
    }

    // ---- storage ----

    [Fact]
    public void Storage_text_with_no_storage_notes_behind_it_is_reported_unless_it_says_there_is_none()
    {
        var none = AiEditorialSourceFacts.Of([], "Mix and bake.", null);

        var invented = new AiEditorialSections { StorageReheating = new AiEditorialText { Text = "Keeps in the fridge." } };
        Assert.Contains(AiEditorialClaimScanner.Scan(invented, none), f => f.Code == AiEditorialClaimScanner.UnsupportedStorageGuidance);

        var honest = new AiEditorialSections { StorageReheating = new AiEditorialText { Text = "No storage guidance supplied." } };
        Assert.Empty(AiEditorialClaimScanner.Scan(honest, none));
    }

    [Fact]
    public void Storage_guidance_in_another_section_is_reported_when_the_recipe_gives_none()
    {
        var none = AiEditorialSourceFacts.Of([], "Mix and bake.", null);
        var faq = new AiEditorialSections
        {
            Faq = [new AiEditorialFaq { Question = "Can I freeze it?", Answer = "Yes, it freezes well." }],
        };

        var finding = Assert.Single(AiEditorialClaimScanner.Scan(faq, none).Where(f => f.Code == AiEditorialClaimScanner.UnsupportedStorageGuidance).Take(1));

        Assert.Equal(AiEditorialSection.Faq, finding.Section);
        Assert.Equal(0, finding.ItemIndex);
    }

    // ---- provenance ----

    [Theory]
    [InlineData("My grandmother made this every autumn.")]
    [InlineData("I tested this five times.")]
    [InlineData("A family favourite.")]
    public void An_experience_or_origin_the_recipe_does_not_record_is_reported(string text)
    {
        Assert.Contains(Scan(Headnote(text)), f => f.Code == AiEditorialClaimScanner.UnsupportedProvenance);
    }

    [Fact]
    public void An_origin_the_recipe_records_is_not_reported()
    {
        var facts = AiEditorialSourceFacts.Of([], "My grandmother's card, never written down.", null);

        Assert.DoesNotContain(
            AiEditorialClaimScanner.Scan(Headnote("My grandmother made this."), facts),
            f => f.Code == AiEditorialClaimScanner.UnsupportedProvenance);
    }

    // ---- shape ----

    [Fact]
    public void Findings_locate_the_item_they_are_about_and_a_clean_package_has_none()
    {
        var sections = new AiEditorialSections
        {
            Tips = [new AiEditorialText { Text = "Rest the dough." }, new AiEditorialText { Text = "Bake for 99 minutes." }],
        };

        var finding = Assert.Single(Scan(sections));

        Assert.Equal(AiEditorialSection.Tips, finding.Section);
        Assert.Equal(1, finding.ItemIndex);
        Assert.Empty(Scan(new AiEditorialSections()));
    }

    // ---- context: a figure is supported by the same kind of figure, not any figure ----

    [Fact]
    public void A_duration_is_supported_only_by_the_same_duration_in_the_same_unit()
    {
        var facts = AiEditorialSourceFacts.Of([], "Use 3 eggs. Refrigerate up to 3 days.", "Refrigerate up to 3 days.");

        Assert.Empty(AiEditorialClaimScanner.Scan(Headnote("Refrigerate up to 3 days."), facts));
        Assert.Single(AiEditorialClaimScanner.Scan(Headnote("Refrigerate up to 3 weeks."), facts));
        Assert.Single(AiEditorialClaimScanner.Scan(Headnote("Bake 3 minutes."), facts));
    }

    [Fact]
    public void A_quantity_elsewhere_in_the_recipe_does_not_support_a_storage_duration()
    {
        var facts = AiEditorialSourceFacts.Of([], "Use 3 eggs and bake at 350F.", "Keeps in a tin.");
        var storage = new AiEditorialSections { StorageReheating = new AiEditorialText { Text = "Keeps in a tin for 350 days." } };

        Assert.Contains(AiEditorialClaimScanner.Scan(storage, facts), f => f.Code == AiEditorialClaimScanner.UnsupportedNumber);
    }

    [Theory]
    [InlineData("Ready in thirty minutes.")]
    [InlineData("Rest for twenty-four hours.")]
    [InlineData("A two-hour rest helps.")]
    [InlineData("Give it half an hour.")]
    [InlineData("Keeps a couple of weeks.")]
    public void Numbers_written_as_words_before_a_unit_are_found_beyond_twelve_and_in_compounds(string text)
    {
        Assert.Contains(Scan(Headnote(text)), f => f.Code == AiEditorialClaimScanner.UnsupportedNumber);
    }

    // ---- storage ----

    [Fact]
    public void Storage_words_are_supported_by_the_storage_notes_alone_never_by_a_step()
    {
        var facts = AiEditorialSourceFacts.Of([], "Freeze the dough for 1 hour, then bake.", "Keeps in a tin.");
        var tip = new AiEditorialSections { Tips = [new AiEditorialText { Text = "Freeze leftovers for later." }] };

        Assert.Contains(AiEditorialClaimScanner.Scan(tip, facts), f => f.Code == AiEditorialClaimScanner.UnsupportedStorageGuidance);
    }

    [Fact]
    public void Storage_text_that_adds_to_what_the_notes_say_is_reported()
    {
        var facts = AiEditorialSourceFacts.Of([], null, "Refrigerate up to 3 days.");
        var storage = new AiEditorialSections { StorageReheating = new AiEditorialText { Text = "Refrigerate up to 3 days, or freeze for months." } };

        Assert.Contains(AiEditorialClaimScanner.Scan(storage, facts), f => f.Code == AiEditorialClaimScanner.UnsupportedStorageGuidance);
    }

    [Fact]
    public void The_no_guidance_sentence_must_stand_alone_not_be_stapled_to_guidance()
    {
        var none = AiEditorialSourceFacts.Of([], "Mix and bake.", null);
        var stapled = new AiEditorialSections { StorageReheating = new AiEditorialText { Text = "Keeps well for a week. No storage guidance supplied." } };

        Assert.Contains(AiEditorialClaimScanner.Scan(stapled, none), f => f.Code == AiEditorialClaimScanner.UnsupportedStorageGuidance);
    }

    // ---- support must be the creator's own words, about the dish ----

    [Fact]
    public void An_ingredient_line_does_not_make_a_claim_about_the_finished_dish()
    {
        // The flour is gluten-free; nothing the creator wrote says the loaf is.
        var facts = AiEditorialSourceFacts.Of([], "A dense loaf.", null);

        Assert.Empty(AiEditorialClaimScanner.Scan(Headnote("A dense loaf."), facts));
        Assert.Contains(AiEditorialClaimScanner.Scan(Headnote("A gluten-free loaf."), facts), f => f.Code == AiEditorialClaimScanner.UnsupportedSafetyClaim);
    }

    [Theory]
    [InlineData("This is not vegan.", "A vegan loaf.")]
    [InlineData("A non-vegan bake.", "A vegan loaf.")]
    [InlineData("Quite unhealthy, honestly.", "A healthy loaf.")]
    public void A_negation_or_a_longer_word_in_the_creators_prose_does_not_support_the_claim(string prose, string generated)
    {
        var facts = AiEditorialSourceFacts.Of([], prose, null);

        Assert.Contains(AiEditorialClaimScanner.Scan(Headnote(generated), facts), f => f.Code == AiEditorialClaimScanner.UnsupportedSafetyClaim);
    }

    // ---- formatting cannot hide a claim or crash the scan ----

    [Theory]
    [InlineData("A gluten\u2011free loaf.")]
    [InlineData("A gluten\u2013free loaf.")]
    [InlineData("A gluten-\u200Bfree loaf.")]
    [InlineData("Safe\u00A0to\u00A0eat.")]
    [InlineData("Safe  to   eat.")]
    [InlineData("A GF loaf.")]
    public void Dashes_spaces_and_invisible_characters_do_not_hide_a_claim(string text)
    {
        // A source that makes none of these claims: the shared one mentions gluten-free flour.
        var neutral = AiEditorialSourceFacts.Of([], "A dense loaf.", null);

        Assert.Contains(Scan(Headnote(text), neutral), f => f.Code == AiEditorialClaimScanner.UnsupportedSafetyClaim);
    }

    [Fact]
    public void Fullwidth_digits_and_vulgar_fractions_are_read_as_the_figures_they_are()
    {
        Assert.Single(Scan(Headnote("Keeps \uFF15 days.")), f => f.Code == AiEditorialClaimScanner.UnsupportedNumber);

        var facts = AiEditorialSourceFacts.Of(["2", "1/2"], "Use 2\u00BD cups.", null);
        Assert.Empty(AiEditorialClaimScanner.Scan(Headnote("Use 2\u00BD cups."), facts));
        Assert.NotEmpty(AiEditorialClaimScanner.Scan(Headnote("Use \u00BE cup."), facts));
    }

    [Theory]
    [InlineData("Use \uFF15\u2044\uFF13 cups.")]
    [InlineData("Use 123456789012345678901234567890 cups.")]
    [InlineData("Use 1/0 cups.")]
    [InlineData("Use 99999999999999999999999999999999999/3 cups.")]
    public void Pathological_numbers_are_reported_never_thrown_on(string text)
    {
        var findings = Scan(Headnote(text));

        Assert.NotNull(findings);
    }

    // ---- units: a figure is supported only in the unit the recipe gave it ----

    [Fact]
    public void A_temperature_in_the_other_scale_is_reported()
    {
        var facts = AiEditorialSourceFacts.Of([], "Bake at 350°F for a crisp crust.", null);

        var finding = Assert.Single(AiEditorialClaimScanner.Scan(Headnote("Bake at 350°C."), facts));

        Assert.Equal(AiEditorialClaimScanner.UnsupportedNumber, finding.Code);
        Assert.Empty(AiEditorialClaimScanner.Scan(Headnote("Bake at 350°F."), facts));
        Assert.Empty(AiEditorialClaimScanner.Scan(Headnote("Bake at 350 F."), facts));
    }

    [Fact]
    public void A_temperature_with_no_scale_written_agrees_with_either()
    {
        var facts = AiEditorialSourceFacts.Of([], "Bake at 350°F.", null);

        Assert.Empty(AiEditorialClaimScanner.Scan(Headnote("Bake at 350 degrees."), facts));
    }

    [Fact]
    public void A_structured_temperature_with_no_scale_known_supports_either_scale_but_never_another_number()
    {
        var snapshot = new RecipeSnapshotDocument
        {
            SchemaVersion = RecipeSnapshotDocument.CurrentSchemaVersion,
            Recipe = new RecipeSnapshotHeader { Title = "Loaf" },
            InstructionGroups =
            [
                new RecipeSnapshotInstructionGroup
                {
                    Id = Guid.NewGuid(),
                    Steps = [new RecipeSnapshotInstructionStep { Id = Guid.NewGuid(), Text = "Bake.", TemperatureValue = 350m }],
                },
            ],
        };
        var facts = AiEditorialSourceFacts.From(snapshot);

        Assert.Empty(AiEditorialClaimScanner.Scan(Headnote("Bake at 350°C."), facts));
        Assert.Single(AiEditorialClaimScanner.Scan(Headnote("Bake at 360°F."), facts));
    }

    private static AiEditorialSourceFacts ButterSource() => AiEditorialSourceFacts.From(new RecipeSnapshotDocument
    {
        SchemaVersion = RecipeSnapshotDocument.CurrentSchemaVersion,
        Recipe = new RecipeSnapshotHeader { Title = "Cookies", ServingCount = 12m },
        IngredientGroups =
        [
            new RecipeSnapshotIngredientGroup
            {
                Id = Guid.NewGuid(),
                Ingredients =
                [
                    new RecipeSnapshotIngredient { Id = Guid.NewGuid(), SortOrder = 0, DisplayText = "2 tbsp butter", Quantity = 2m, UnitText = "tbsp" },
                    new RecipeSnapshotIngredient { Id = Guid.NewGuid(), SortOrder = 1, DisplayText = "3 eggs", Quantity = 3m },
                ],
            },
        ],
        InstructionGroups =
        [
            new RecipeSnapshotInstructionGroup
            {
                Id = Guid.NewGuid(),
                Steps = [new RecipeSnapshotInstructionStep { Id = Guid.NewGuid(), Text = "Chill for 24 hours.", DurationMinutes = 1440 }],
            },
        ],
    });

    [Fact]
    public void A_measured_amount_in_another_unit_is_reported()
    {
        var facts = ButterSource();

        var finding = Assert.Single(AiEditorialClaimScanner.Scan(Headnote("Use 2 cups of butter."), facts));

        Assert.Equal(AiEditorialClaimScanner.UnsupportedNumber, finding.Code);
        Assert.Empty(AiEditorialClaimScanner.Scan(Headnote("Use 2 tbsp of butter."), facts));
        Assert.Empty(AiEditorialClaimScanner.Scan(Headnote("Use 2 tablespoons of butter."), facts));
        Assert.Empty(AiEditorialClaimScanner.Scan(Headnote("Use two tablespoons of butter."), facts));
    }

    [Fact]
    public void A_measured_amount_does_not_support_a_count_nor_a_count_a_measure()
    {
        var facts = ButterSource();

        // "2 tbsp" is in the recipe; a bare 2 is not, because the only bare figures are 3 eggs and 12 servings.
        Assert.Single(AiEditorialClaimScanner.Scan(Headnote("Makes 2 dozen."), facts));
        Assert.Empty(AiEditorialClaimScanner.Scan(Headnote("Uses 3 eggs."), facts));
        Assert.Single(AiEditorialClaimScanner.Scan(Headnote("Use 3 cups of flour."), facts));
    }

    [Fact]
    public void A_duration_does_not_support_a_count()
    {
        var facts = ButterSource();

        // 24 hours is stated, so 24 hours is fine; "makes 24 cookies" is a yield the recipe never states.
        Assert.Empty(AiEditorialClaimScanner.Scan(Headnote("Chill for 24 hours."), facts));
        Assert.Single(AiEditorialClaimScanner.Scan(Headnote("Makes 24 cookies."), facts));
        Assert.Empty(AiEditorialClaimScanner.Scan(Headnote("Makes 12 cookies."), facts));
    }

    // ---- advice the recipe did not give ----

    [Theory]
    [InlineData("Suitable for vegans.")]
    [InlineData("Contains no gluten.")]
    [InlineData("Made without dairy.")]
    [InlineData("It is fine to taste the raw batter.")]
    [InlineData("Rinse the chicken first.")]
    [InlineData("Perfect for diabetics.")]
    [InlineData("A low-glycemic treat.")]
    [InlineData("The eggs can stay out while you prep.")]
    [InlineData("Not advised during pregnancy.")]
    public void Advice_and_suitability_claims_the_recipe_did_not_make_are_reported(string text)
    {
        var findings = Scan(Headnote(text)).Where(f => f.Code == AiEditorialClaimScanner.UnsupportedSafetyClaim).ToList();

        Assert.NotEmpty(findings);
        Assert.All(findings, finding => Assert.Equal(AiWarningKind.SafetyCaution, finding.Kind));
    }

    [Theory]
    [InlineData("Perfect for weeknights.")]
    [InlineData("A great way to use up stale bread.")]
    [InlineData("Fine for a crowd.")]
    [InlineData("No fuss and no waiting.")]
    public void Ordinary_praise_is_not_a_safety_claim(string text)
    {
        Assert.DoesNotContain(Scan(Headnote(text)), f => f.Code == AiEditorialClaimScanner.UnsupportedSafetyClaim);
    }

    [Fact]
    public void A_claim_the_creator_made_in_their_own_words_is_not_reported()
    {
        var facts = AiEditorialSourceFacts.Of([], "Suitable for vegans. Contains no gluten.", null);

        Assert.Empty(AiEditorialClaimScanner.Scan(Headnote("Suitable for vegans, and it contains no gluten."), facts));
    }
}

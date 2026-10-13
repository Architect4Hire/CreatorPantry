using CreatorPantry.Domain.Modules.Ai.Facade;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.Content.Managers;

namespace CreatorPantry.Tests.Content;

/// <summary>
/// The long-form claim scanner (LF.3.2) on its own: text in, findings out. No database, no model, no clock.
/// </summary>
/// <remarks>
/// The frames it shares with the editorial and SEO scanners are reached through the AI module's real facade,
/// so a test here fails if those scanners and this one's reading of them ever part company. That a body is
/// scanned on every write, and that a finding never refuses one, is the seam's to show
/// (<c>LongFormOutlineRequestSeamTests</c>).
/// </remarks>
public sealed class LongFormClaimScannerTests
{
    private static readonly IAiClaimScanFacade Scanner = new AiClaimScanFacade();

    private static readonly Guid BreadReference = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly Guid CakeReference = Guid.Parse("77777777-7777-7777-7777-777777777777");

    private static readonly LongFormClaimRecipe Bread = new(
        BreadReference,
        "Buttermilk Soda Bread",
        ["450g plain flour", "400ml buttermilk", "1 tsp bicarbonate of soda", "Bake for 45 minutes.", "Makes 1 loaf", "10 minutes"]);

    private static readonly LongFormClaimRecipe Cake = new(
        CakeReference, "Olive Oil Cake", ["150 ml olive oil", "3 eggs", "Bake for 25 minutes.", "Serves 8"]);

    // ---- Figures ----

    [Fact]
    public void A_figure_the_pinned_recipe_states_is_not_flagged()
    {
        Assert.Empty(Scan("Stir 400ml buttermilk into 450g plain flour, then bake for 45 minutes."));
    }

    [Theory]
    [InlineData("For a crowd, use 900g plain flour.")]
    [InlineData("From cupboard to table in 55 minutes.")]
    [InlineData("You will want about 3 cups of plain flour.")]
    [InlineData("Bake at 220°C until it sounds hollow.")]
    [InlineData("Each slice comes in at only 120 calories.")]
    public void A_figure_the_pinned_recipe_does_not_state_is_flagged_whether_doubled_summed_converted_or_invented(string body)
    {
        Assert.Contains(Codes(body), code => code == AiEditorialClaimScanner.UnsupportedNumber);
    }

    [Fact]
    public void With_no_pinned_recipe_every_figure_is_one_nothing_supports()
    {
        var findings = LongFormClaimScanner.Scan(
            Scanner, LongFormSectionRole.Body, null, "Use 500g flour and bake for 30 minutes.", recipes: []);

        Assert.Contains(findings, finding => finding.Code == AiEditorialClaimScanner.UnsupportedNumber);

        // And prose with no figure in it has nothing to answer for.
        Assert.Empty(LongFormClaimScanner.Scan(
            Scanner, LongFormSectionRole.Body, null, "Baking on a grey morning is its own reward.", recipes: []));
    }

    [Fact]
    public void A_section_bound_to_one_recipe_is_held_to_that_recipes_figures_and_not_its_neighbours()
    {
        // 25 minutes is the cake's. In the bread's section it is a figure the bread does not state.
        var borrowed = LongFormClaimScanner.Scan(
            Scanner, LongFormSectionRole.Item, BreadReference, "Mix, then bake for 25 minutes.", [Bread, Cake]);
        Assert.Contains(borrowed, finding => finding.Code == AiEditorialClaimScanner.UnsupportedNumber);

        var own = LongFormClaimScanner.Scan(
            Scanner, LongFormSectionRole.Item, BreadReference, "Mix, then bake for 45 minutes.", [Bread, Cake]);
        Assert.DoesNotContain(own, finding => finding.Code == AiEditorialClaimScanner.UnsupportedNumber);

        // A section bound to nothing answers to every recipe the piece is pinned to.
        var shared = LongFormClaimScanner.Scan(
            Scanner, LongFormSectionRole.Lead, null, "One bakes for 45 minutes and the other for 25 minutes.", [Bread, Cake]);
        Assert.DoesNotContain(shared, finding => finding.Code == AiEditorialClaimScanner.UnsupportedNumber);
    }

    [Theory]
    [InlineData("1. Mix.\n2. Stir.\n3. Bake.", "Mix.\nStir.\nBake.")]
    [InlineData("Some text.\n375) is the temperature.", "Some text.\n375) is the temperature.")]
    [InlineData("1. Mix.\n900. grams of flour go in.", "Mix.\n900. grams of flour go in.")]
    [InlineData("1. One list.\n\nProse.\n\n1. Another.\n2. Its second.", "One list.\n\nProse.\n\nAnother.\nIts second.")]
    public void Only_the_numbers_of_a_numbered_list_are_kept_from_the_figure_check(string body, string scanned)
    {
        Assert.Equal(scanned, LongFormClaimScanner.WithoutListNumbers(body));
    }

    [Fact]
    public void A_numbered_list_is_not_a_list_of_figures_and_a_line_that_opens_with_one_is_still_read()
    {
        Assert.Empty(Scan("1. Mix the dry ingredients.\n2. Stir in the buttermilk.\n3. Bake for 45 minutes."));
        Assert.Contains(Codes("Heat the oven first.\n375) is the temperature I use."), code => code == AiEditorialClaimScanner.UnsupportedNumber);
    }

    // ---- The frames carried over from the editorial and SEO scanners ----

    [Theory]
    [InlineData("Yes, it is perfectly safe for babies and toddlers.", AiEditorialClaimScanner.UnsupportedSafetyClaim)]
    [InlineData("This loaf is completely nut-free and safe for anyone with an allergy.", AiEditorialClaimScanner.UnsupportedSafetyClaim)]
    [InlineData("It is suitable for diabetics and a healthy choice for the whole family.", AiEditorialClaimScanner.UnsupportedSafetyClaim)]
    [InlineData("It lowers cholesterol and is good for your heart health.", AiEditorialClaimScanner.UnsupportedSafetyClaim)]
    [InlineData("It freezes beautifully for months.", AiEditorialClaimScanner.UnsupportedStorageGuidance)]
    [InlineData("Keeps well on the counter at room temperature, left out overnight.", AiEditorialClaimScanner.UnsupportedStorageGuidance)]
    [InlineData("My grandmother handed this recipe down to me.", AiEditorialClaimScanner.UnsupportedProvenance)]
    [InlineData("Soda bread is the most searched bread recipe this year.", AiSeoClaimScanner.UnsupportedMetric)]
    public void Each_claim_frame_the_existing_scanners_catch_is_caught_here(string body, string code)
    {
        Assert.Contains(code, Codes(body));
    }

    [Fact]
    public void A_dish_called_by_the_name_its_creator_gave_it_is_not_making_a_claim()
    {
        var named = new LongFormClaimRecipe(BreadReference, "Gluten-Free Seeded Loaf", ["300g gluten-free flour blend"]);

        var findings = LongFormClaimScanner.Scan(
            Scanner, LongFormSectionRole.Hook, null, "Gluten-Free Seeded Loaf is the bread I make most weeks.", [named]);

        Assert.DoesNotContain(findings, finding => finding.Code == AiEditorialClaimScanner.UnsupportedSafetyClaim);
    }

    // ---- What only long-form adds ----

    [Theory]
    [InlineData("This loaf is foolproof.", true)]
    [InlineData("It never fails.", true)]
    [InlineData("Follow the steps and it works every time.", true)]
    [InlineData("You can't go wrong with this one.", true)]
    [InlineData("Most loaves fail because the oven was not hot.", false)]
    [InlineData("I have failed at this more than once.", false)]
    public void A_promise_that_the_recipe_will_work_is_a_frame_and_talking_about_failure_is_not(string body, bool promised)
    {
        Assert.Equal(promised, Codes(body).Contains(LongFormClaimScanner.UnsupportedGuarantee));
    }

    [Theory]
    [InlineData("The creator has not given storage notes for this loaf.", true)]
    [InlineData("There are no storage or reheating notes for this recipe.", true)]
    [InlineData("Reheat gently in a low oven until warm.", false)]
    [InlineData("Store it in the pantry for a week.", false)]
    [InlineData("The creator has not given storage notes, but it freezes well.", false)]
    public void A_storage_section_is_forgiven_only_for_saying_there_are_no_storage_notes(string body, bool forgiven)
    {
        Assert.Equal(forgiven, LongFormClaimScanner.OnlySaysThereAreNoStorageNotes(Scanner, body));

        var storage = LongFormClaimScanner.Scan(Scanner, LongFormSectionRole.Storage, null, body, [Bread]);
        Assert.Equal(!forgiven, storage.Any(finding => finding.Code == AiEditorialClaimScanner.UnsupportedStorageGuidance));
    }

    [Fact]
    public void The_storage_allowance_is_the_storage_sections_alone()
    {
        var elsewhere = LongFormClaimScanner.Scan(
            Scanner, LongFormSectionRole.Tips, null, "The creator has not given storage notes for this loaf.", [Bread]);

        Assert.Contains(elsewhere, finding => finding.Code == AiEditorialClaimScanner.UnsupportedStorageGuidance);
    }

    // ---- What a finding is ----

    [Fact]
    public void A_blank_body_has_nothing_to_find_and_the_same_sentence_is_not_said_twice()
    {
        Assert.Empty(LongFormClaimScanner.Scan(Scanner, LongFormSectionRole.Hook, null, "   \n ", [Bread]));
        Assert.Empty(LongFormClaimScanner.Scan(Scanner, LongFormSectionRole.Hook, null, null, [Bread]));

        var repeated = Scan("It never fails. Truly, it never fails.");
        Assert.Single(repeated, finding => finding.Code == LongFormClaimScanner.UnsupportedGuarantee);
    }

    [Fact]
    public void Scanning_is_the_same_every_time_and_says_what_kind_of_thing_each_finding_is()
    {
        const string body = "Use 900g flour. It is perfectly safe for babies.";

        var first = Scan(body);
        var second = Scan(body);

        Assert.Equal(first, second);
        Assert.Contains(first, finding => finding.Kind is LongFormFindingKind.SafetyCaution);
        Assert.Contains(first, finding => finding.Kind is LongFormFindingKind.UnverifiedClaim);
        Assert.All(first, finding => Assert.False(string.IsNullOrWhiteSpace(finding.Message)));
    }

    private static IReadOnlyList<LongFormSectionFinding> Scan(string body) =>
        LongFormClaimScanner.Scan(Scanner, LongFormSectionRole.Body, null, body, [Bread]);

    private static List<string> Codes(string body) => [.. Scan(body).Select(finding => finding.Code)];
}

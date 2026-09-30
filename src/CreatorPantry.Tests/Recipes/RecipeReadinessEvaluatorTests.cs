using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.Ingredients.Managers;
using CreatorPantry.Domain.Modules.Recipes.Managers;

namespace CreatorPantry.Tests.Recipes;

/// <summary>
/// Every readiness rule, one at a time, against a pure function.
/// </summary>
/// <remarks>
/// <para>
/// No database and no fakes: <see cref="RecipeReadinessEvaluator"/> takes facts and returns findings, so a rule can
/// be exercised by stating the one fact it reads. That is the whole reason the evaluator is pure — a rule needing a
/// seeded recipe to test would be a rule nobody tests at the boundaries.
/// </para>
/// <para>
/// Each test starts from <see cref="Ready"/>, a recipe that satisfies every rule, and breaks exactly one thing.
/// So a rule firing when it should not shows up as a second unexpected finding, and a rule silently deleted shows
/// up in <see cref="Every_catalogue_rule_is_implemented_and_reported"/>.
/// </para>
/// </remarks>
public sealed class RecipeReadinessEvaluatorTests
{
    private static readonly Guid RecipeId = Guid.NewGuid();
    private static readonly Guid VersionId = Guid.NewGuid();
    private static readonly Guid TestRunId = Guid.NewGuid();
    private static readonly Guid ProposalId = Guid.NewGuid();

    // ---- The catalogue itself ----

    /// <summary>
    /// Every rule in the catalogue produces a finding, and none produces two.
    /// </summary>
    /// <remarks>
    /// The test that makes the twenty below trustworthy: a rule listed in the catalogue and missing from the
    /// evaluator's switch would otherwise throw only when something happened to reach it, and a rule reported twice
    /// would double-count a blocker.
    /// </remarks>
    [Fact]
    public void Every_catalogue_rule_is_implemented_and_reported()
    {
        var result = Evaluate(Ready());

        Assert.Equal(
            RecipeReadinessCatalogue.Rules.Select(rule => rule.Id),
            result.Findings.Select(finding => finding.RuleId));
    }

    [Fact]
    public void Rule_ids_are_unique()
    {
        Assert.Equal(
            RecipeReadinessCatalogue.Rules.Count,
            RecipeReadinessCatalogue.Rules.Select(rule => rule.Id).Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// A fully prepared recipe clears every rule, and the one rule that does not apply says so rather than passing.
    /// </summary>
    [Fact]
    public void A_complete_recipe_has_no_blockers_and_no_recommendations()
    {
        var result = Evaluate(Ready());

        Assert.False(result.HasBlockers);
        Assert.Equal(0, result.BlockerCount);
        Assert.Equal(0, result.RecommendationCount);
        Assert.Equal(RecipeReadinessCatalogue.Version, result.RuleSetVersion);

        // Attribution is NotApplicable, not Satisfied: this recipe cites no source, so nothing was checked.
        Assert.Equal(
            RecipeReadinessStatus.NotApplicable,
            Finding(result, RecipeReadinessCatalogue.AttributionPresent).Status);

        Assert.All(
            result.Findings.Where(finding => finding.RuleId != RecipeReadinessCatalogue.AttributionPresent),
            finding => Assert.Equal(RecipeReadinessStatus.Satisfied, finding.Status));
    }

    /// <summary>
    /// The evaluation names the version and the state it judged, which is what lets a later approval prove it is
    /// approving the recipe that was evaluated.
    /// </summary>
    [Fact]
    public void The_result_names_the_version_and_the_state_it_evaluated()
    {
        var result = Evaluate(Ready());

        Assert.Equal(RecipeId, result.RecipeId);
        Assert.Equal(VersionId, result.EvaluatedVersionId);
        Assert.Equal(4, result.EvaluatedVersionNumber);
        Assert.Equal(RecipeConcurrencyToken.From([1, 2, 3, 4, 5, 6, 7, 8]), result.ConcurrencyToken);
    }

    // ---- Required fields ----

    [Fact]
    public void A_recipe_with_no_ingredient_lines_is_blocked()
    {
        var result = Evaluate(Ready() with { IngredientLines = [] });

        var finding = Blocker(result, RecipeReadinessCatalogue.IngredientsPresent);

        Assert.Equal(RecipeReadinessEvidenceKind.Recipe, Assert.Single(finding.Evidence).Kind);
    }

    [Fact]
    public void A_recipe_with_no_instruction_steps_is_blocked()
    {
        var result = Evaluate(Ready() with { InstructionStepCount = 0 });

        Blocker(result, RecipeReadinessCatalogue.InstructionsPresent);
    }

    /// <summary>
    /// A yield is stated by any one of its three separable facts, which is what recipes.md makes them: the
    /// creator's wording, the measured batch, or the serving count. A recipe that records only how many it serves
    /// has said what it makes.
    /// </summary>
    [Fact]
    public void Any_one_of_the_three_yield_facts_satisfies_the_yield_rule()
    {
        var withoutYield = Ready() with
        {
            YieldText = null,
            YieldQuantity = null,
            YieldUnitId = null,
            ServingCount = null,
        };

        Blocker(Evaluate(withoutYield), RecipeReadinessCatalogue.YieldStated);

        Satisfied(
            Evaluate(withoutYield with { YieldText = "makes 12 muffins" }),
            RecipeReadinessCatalogue.YieldStated);
        Satisfied(
            Evaluate(withoutYield with { YieldQuantity = 2m, YieldUnitId = Guid.NewGuid() }),
            RecipeReadinessCatalogue.YieldStated);
        Satisfied(
            Evaluate(withoutYield with { ServingCount = 12m }),
            RecipeReadinessCatalogue.YieldStated);
    }

    /// <summary>A quantity with no unit is not a measured batch, so it does not satisfy the rule on its own.</summary>
    [Fact]
    public void A_yield_quantity_with_no_unit_does_not_state_a_yield()
    {
        var result = Evaluate(Ready() with
        {
            YieldText = null,
            ServingCount = null,
            YieldQuantity = 2m,
            YieldUnitId = null,
        });

        Blocker(result, RecipeReadinessCatalogue.YieldStated);
    }

    [Theory]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, false, false, true)]
    public void Any_one_stated_time_satisfies_the_time_rule(bool total, bool prep, bool cook, bool rest)
    {
        var facts = Ready() with
        {
            TotalTimeMinutes = total ? 40 : null,
            PrepTimeMinutes = prep ? 10 : null,
            CookTimeMinutes = cook ? 30 : null,
            RestTimeMinutes = rest ? 15 : null,
        };

        Satisfied(Evaluate(facts), RecipeReadinessCatalogue.TimeStated);
    }

    [Fact]
    public void A_recipe_stating_no_times_is_blocked()
    {
        var result = Evaluate(Ready() with
        {
            TotalTimeMinutes = null,
            PrepTimeMinutes = null,
            CookTimeMinutes = null,
            RestTimeMinutes = null,
        });

        Blocker(result, RecipeReadinessCatalogue.TimeStated);
    }

    /// <summary>
    /// The rule never adds the parts up. A total smaller than their sum is a real kitchen overlapping steps, not a
    /// defect — so a recipe stating both is satisfied, whatever the arithmetic says.
    /// </summary>
    [Fact]
    public void The_time_rule_does_not_reconcile_the_total_against_the_parts()
    {
        var result = Evaluate(Ready() with
        {
            PrepTimeMinutes = 30,
            CookTimeMinutes = 60,
            RestTimeMinutes = 30,
            TotalTimeMinutes = 45,
        });

        Satisfied(result, RecipeReadinessCatalogue.TimeStated);
    }

    [Fact]
    public void A_recipe_with_no_description_is_recommended_against_not_blocked()
    {
        var result = Evaluate(Ready() with { Description = null });

        Recommendation(result, RecipeReadinessCatalogue.DescriptionPresent);
        Assert.False(result.HasBlockers);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Whitespace_is_not_a_description(string description)
    {
        Recommendation(Evaluate(Ready() with { Description = description }), RecipeReadinessCatalogue.DescriptionPresent);
    }

    /// <summary>
    /// A rights question, not a polish one: publishing someone else's recipe with a machine-readable URL and no
    /// words crediting them is the failure this blocks.
    /// </summary>
    [Fact]
    public void A_recipe_citing_a_source_with_no_credit_is_blocked()
    {
        var result = Evaluate(Ready() with { SourceUrl = "https://example.com/cake", AttributionText = null });

        Blocker(result, RecipeReadinessCatalogue.AttributionPresent);
    }

    [Fact]
    public void A_recipe_citing_a_source_and_crediting_it_satisfies_the_rule()
    {
        var result = Evaluate(Ready() with
        {
            SourceUrl = "https://example.com/cake",
            AttributionText = "Adapted from Example Kitchen.",
        });

        Satisfied(result, RecipeReadinessCatalogue.AttributionPresent);
    }

    /// <summary>
    /// An original recipe has not <em>passed</em> an attribution check — there was nothing to check. That
    /// distinction is the whole reason <see cref="RecipeReadinessStatus.NotApplicable"/> exists.
    /// </summary>
    [Fact]
    public void A_recipe_citing_no_source_finds_the_attribution_rule_inapplicable()
    {
        var result = Evaluate(Ready() with { SourceUrl = null, AttributionText = null });

        Assert.Equal(
            RecipeReadinessStatus.NotApplicable,
            Finding(result, RecipeReadinessCatalogue.AttributionPresent).Status);
    }

    // ---- Ingredient resolution ----

    /// <summary>
    /// Ambiguous blocks because the creator can clear it: the line has candidates to choose between. Evidence names
    /// the line by the creator's own text rather than by an id or a normalized form.
    /// </summary>
    [Fact]
    public void An_ambiguous_ingredient_line_is_blocked_and_named_in_the_creators_words()
    {
        var result = Evaluate(Ready() with
        {
            IngredientLines =
            [
                Line("200g flour", IngredientMatchStatus.Matched),
                Line("a knob of butter", IngredientMatchStatus.Ambiguous, ingredientId: null),
            ],
        });

        var finding = Blocker(result, RecipeReadinessCatalogue.IngredientsAmbiguous);
        var evidence = Assert.Single(finding.Evidence);

        Assert.Equal(RecipeReadinessEvidenceKind.RecipeIngredientLine, evidence.Kind);
        Assert.Equal("a knob of butter", evidence.Label);
        Assert.Contains("One ingredient line matches", finding.Detail);
    }

    /// <summary>
    /// Unrecognised only recommends, because a line the vocabulary does not know has nothing to resolve to —
    /// blocking on it would be a bar the creator cannot clear.
    /// </summary>
    [Theory]
    [InlineData(IngredientMatchStatus.NoMatch)]
    [InlineData(IngredientMatchStatus.NotAttempted)]
    public void An_unrecognised_ingredient_line_is_recommended_against_not_blocked(IngredientMatchStatus status)
    {
        var result = Evaluate(Ready() with
        {
            IngredientLines = [Line("grandmother's spice mix", status, ingredientId: null)],
        });

        Recommendation(result, RecipeReadinessCatalogue.IngredientsUnrecognized);
        Assert.False(result.HasBlockers);
    }

    [Fact]
    public void The_detail_counts_every_offending_line_and_the_evidence_names_each_one()
    {
        var result = Evaluate(Ready() with
        {
            IngredientLines =
            [
                Line("a knob of butter", IngredientMatchStatus.Ambiguous, ingredientId: null),
                Line("a splash of oil", IngredientMatchStatus.Ambiguous, ingredientId: null),
            ],
        });

        var finding = Blocker(result, RecipeReadinessCatalogue.IngredientsAmbiguous);

        Assert.Equal(2, finding.Evidence.Count);
        Assert.Contains("2 ingredient lines match", finding.Detail);
    }

    // ---- Outstanding AI work ----

    /// <summary>
    /// A caution about the food blocks, and the model's own words are quoted rather than paraphrased — a caution
    /// summarized is a caution changed.
    /// </summary>
    [Theory]
    [InlineData(AiWarningKind.SafetyCaution)]
    [InlineData(AiWarningKind.CulinaryCaution)]
    public void An_unanswered_safety_or_culinary_caution_is_blocked(AiWarningKind kind)
    {
        var proposalId = Guid.NewGuid();

        var result = Evaluate(
            Ready(),
            new RecipeReadinessExternalFacts(
                new AiOutstandingSummaryServiceModel(
                    1, [proposalId], [new AiOutstandingWarningServiceModel(kind, "Check the internal temperature.", proposalId)]),
                []));

        var finding = Blocker(result, RecipeReadinessCatalogue.AiSafetyCautionOutstanding);
        var evidence = Assert.Single(finding.Evidence);

        Assert.Equal(RecipeReadinessEvidenceKind.AiProposal, evidence.Kind);
        Assert.Equal(proposalId, evidence.RecordId);
        Assert.Equal("Check the internal temperature.", evidence.Label);
    }

    /// <summary>
    /// The other kinds are the model explaining itself, so they advise. This is also the test that proves the two
    /// AI warning rules do not both fire on one warning.
    /// </summary>
    [Theory]
    [InlineData(AiWarningKind.Assumption)]
    [InlineData(AiWarningKind.UnverifiedClaim)]
    [InlineData(AiWarningKind.UnresolvedQuestion)]
    [InlineData(AiWarningKind.NonScalableLanguage)]
    [InlineData(AiWarningKind.Limitation)]
    [InlineData(AiWarningKind.Rationale)]
    [InlineData(AiWarningKind.Unspecified)]
    public void Any_other_unanswered_warning_is_recommended_against_not_blocked(AiWarningKind kind)
    {
        var result = Evaluate(
            Ready(),
            new RecipeReadinessExternalFacts(
                new AiOutstandingSummaryServiceModel(
                    1, [Guid.NewGuid()], [new AiOutstandingWarningServiceModel(kind, "Assumed plain flour.", Guid.NewGuid())]),
                []));

        Recommendation(result, RecipeReadinessCatalogue.AiWarningOutstanding);
        Satisfied(result, RecipeReadinessCatalogue.AiSafetyCautionOutstanding);

        // The pending change is its own recommendation; neither warning rule is a blocker here.
        Assert.False(result.HasBlockers);
    }

    [Fact]
    public void Undecided_proposed_changes_are_recommended_against()
    {
        var result = Evaluate(
            Ready(),
            new RecipeReadinessExternalFacts(new AiOutstandingSummaryServiceModel(3, [ProposalId], []), []));

        var finding = Recommendation(result, RecipeReadinessCatalogue.AiChangesPending);

        Assert.Contains("3 AI-proposed changes are", finding.Detail);
    }

    [Fact]
    public void A_recipe_with_no_outstanding_ai_work_satisfies_all_three_ai_rules()
    {
        var result = Evaluate(Ready(), RecipeReadinessExternalFacts.None);

        Satisfied(result, RecipeReadinessCatalogue.AiSafetyCautionOutstanding);
        Satisfied(result, RecipeReadinessCatalogue.AiWarningOutstanding);
        Satisfied(result, RecipeReadinessCatalogue.AiChangesPending);
    }

    // ---- Allergen records ----

    /// <summary>
    /// A recommendation, and worded as a statement about records. The detail must say so in as many words, because
    /// a blocker phrased around allergens would read as a safety clearance — which recipes.md forbids this being.
    /// </summary>
    [Theory]
    [InlineData(IngredientAllergenReviewState.NoTraitsRecorded)]
    [InlineData(IngredientAllergenReviewState.AwaitingReview)]
    [InlineData(IngredientAllergenReviewState.PresenceUncertain)]
    public void An_incomplete_allergen_record_is_recommended_against_and_never_a_claim_about_food(
        IngredientAllergenReviewState state)
    {
        var ingredientId = Guid.NewGuid();

        var result = Evaluate(
            Ready(),
            new RecipeReadinessExternalFacts(
                new AiOutstandingSummaryServiceModel(0, [], []),
                [new IngredientAllergenReviewServiceModel(ingredientId, "wheat flour", state)]));

        var finding = Recommendation(result, RecipeReadinessCatalogue.AllergensTraitUnreviewed);

        Assert.False(result.HasBlockers);

        // The sentence says what it is about: the records, not the food.
        Assert.Contains("cannot be stated from reviewed sources", finding.Detail);
        Assert.Contains("not about the food", finding.Detail);

        // And it never claims presence or absence.
        Assert.DoesNotContain("contains", finding.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("free", finding.Detail, StringComparison.OrdinalIgnoreCase);

        var evidence = Assert.Single(finding.Evidence);
        Assert.Equal(RecipeReadinessEvidenceKind.Ingredient, evidence.Kind);
        Assert.Equal(ingredientId, evidence.RecordId);
        Assert.Equal("wheat flour", evidence.Label);
    }

    [Fact]
    public void No_allergen_gap_satisfies_the_allergen_rule()
    {
        Satisfied(Evaluate(Ready(), RecipeReadinessExternalFacts.None), RecipeReadinessCatalogue.AllergensTraitUnreviewed);
    }

    // ---- Test coverage ----

    /// <summary>
    /// Against the exact current version, not "has ever been tested": an edit since the last bake means the words
    /// as they stand are untested, which is the rule the test kitchen exists to serve.
    /// </summary>
    [Fact]
    public void An_untested_current_version_is_blocked_and_names_the_version()
    {
        var result = Evaluate(Ready() with
        {
            TestedCurrentVersion = false,
            LatestTestOutcome = null,
            LatestTestRunId = null,
            OpenIssues = [],
        });

        var finding = Blocker(result, RecipeReadinessCatalogue.TestingCurrentVersionUntested);

        Assert.Contains("version 4", finding.Detail);
        Assert.Equal(RecipeReadinessEvidenceKind.RecipeVersion, Assert.Single(finding.Evidence).Kind);
    }

    /// <summary>
    /// One gap, one blocker. The outcome rule is inapplicable rather than failing, because "untested" and "the last
    /// test failed" are not two problems and a creator should not be shown them as two.
    /// </summary>
    [Fact]
    public void An_untested_version_finds_the_outcome_rule_inapplicable_rather_than_failing_it()
    {
        var result = Evaluate(Ready() with
        {
            TestedCurrentVersion = false,
            LatestTestOutcome = null,
            LatestTestRunId = null,
            OpenIssues = [],
        });

        Assert.Equal(
            RecipeReadinessStatus.NotApplicable,
            Finding(result, RecipeReadinessCatalogue.TestingLastOutcomeFailed).Status);
        Assert.Equal(1, result.BlockerCount);
    }

    [Fact]
    public void A_latest_test_recorded_as_a_failure_is_blocked()
    {
        var result = Evaluate(Ready() with { LatestTestOutcome = TestRunOutcome.Failed });

        var finding = Blocker(result, RecipeReadinessCatalogue.TestingLastOutcomeFailed);

        Assert.Equal(TestRunId, Assert.Single(finding.Evidence).RecordId);
    }

    /// <summary>
    /// It reads the tester's verdict and never derives one: a success recorded alongside things to fix is a
    /// success, because the tester said so.
    /// </summary>
    [Theory]
    [InlineData(TestRunOutcome.Succeeded)]
    [InlineData(TestRunOutcome.SucceededWithIssues)]
    [InlineData(TestRunOutcome.NotStated)]
    public void Any_other_latest_outcome_satisfies_the_outcome_rule(TestRunOutcome outcome)
    {
        Satisfied(Evaluate(Ready() with { LatestTestOutcome = outcome }), RecipeReadinessCatalogue.TestingLastOutcomeFailed);
    }

    /// <summary>
    /// The tester said this stops the recipe, and the evaluation agrees with them rather than re-judging it.
    /// </summary>
    [Fact]
    public void An_unresolved_blocking_issue_is_blocked_and_quotes_the_tester()
    {
        var issueId = Guid.NewGuid();

        var result = Evaluate(Ready() with
        {
            OpenIssues = [new RecipeReadinessOpenIssue(issueId, TestRunId, TestIssueSeverity.Blocking, "Collapsed in the tin")],
        });

        var finding = Blocker(result, RecipeReadinessCatalogue.TestingBlockingIssueOutstanding);
        var evidence = Assert.Single(finding.Evidence);

        Assert.Equal(RecipeReadinessEvidenceKind.TestIssue, evidence.Kind);
        Assert.Equal(issueId, evidence.RecordId);
        Assert.Equal("Collapsed in the tin", evidence.Label);

        // And the lighter rule does not also fire on it.
        Satisfied(result, RecipeReadinessCatalogue.TestingIssueOutstanding);
    }

    [Theory]
    [InlineData(TestIssueSeverity.Major)]
    [InlineData(TestIssueSeverity.Minor)]
    public void An_unresolved_lesser_issue_is_recommended_against_not_blocked(TestIssueSeverity severity)
    {
        var result = Evaluate(Ready() with
        {
            OpenIssues = [new RecipeReadinessOpenIssue(Guid.NewGuid(), TestRunId, severity, "Crumb a little dense")],
        });

        Recommendation(result, RecipeReadinessCatalogue.TestingIssueOutstanding);
        Satisfied(result, RecipeReadinessCatalogue.TestingBlockingIssueOutstanding);
        Assert.False(result.HasBlockers);
    }

    [Fact]
    public void Blocking_and_lesser_issues_are_reported_separately()
    {
        var result = Evaluate(Ready() with
        {
            OpenIssues =
            [
                new RecipeReadinessOpenIssue(Guid.NewGuid(), TestRunId, TestIssueSeverity.Blocking, "Collapsed"),
                new RecipeReadinessOpenIssue(Guid.NewGuid(), TestRunId, TestIssueSeverity.Major, "Too salty"),
                new RecipeReadinessOpenIssue(Guid.NewGuid(), TestRunId, TestIssueSeverity.Minor, "Untidy edges"),
            ],
        });

        Assert.Single(Blocker(result, RecipeReadinessCatalogue.TestingBlockingIssueOutstanding).Evidence);
        Assert.Equal(2, Recommendation(result, RecipeReadinessCatalogue.TestingIssueOutstanding).Evidence.Count);
    }

    // ---- Media ----

    /// <summary>
    /// Advice, not a bar. A recipe is developed and tested before it is photographed, and a hero image is
    /// genuinely required to <em>publish</em> rather than to approve — see the catalogue's own remarks. It was
    /// briefly a blocker, which made approval unreachable for every recipe in the product.
    /// </summary>
    [Fact]
    public void A_recipe_with_no_hero_link_is_recommended_against_not_blocked()
    {
        var result = Evaluate(Ready() with { HasHeroAsset = false, HeroAssetLinkId = null });

        Recommendation(result, RecipeReadinessCatalogue.MediaHeroMissing);
        Assert.False(result.HasBlockers);
    }

    // ---- Publication-facing metadata ----

    [Fact]
    public void A_recipe_naming_no_cuisine_is_recommended_against()
    {
        var result = Evaluate(Ready() with { CuisineId = null });

        var finding = Recommendation(result, RecipeReadinessCatalogue.PublicationCuisineMissing);

        Assert.Equal(nameof(RecipeReadinessFacts.CuisineId), Assert.Single(finding.Evidence).FieldName);
        Assert.False(result.HasBlockers);
    }

    [Fact]
    public void A_recipe_naming_no_course_is_recommended_against()
    {
        Recommendation(Evaluate(Ready() with { CourseId = null }), RecipeReadinessCatalogue.PublicationCourseMissing);
    }

    [Fact]
    public void A_recipe_carrying_no_tags_is_recommended_against()
    {
        Recommendation(Evaluate(Ready() with { TagCount = 0 }), RecipeReadinessCatalogue.PublicationTagsMissing);
    }

    // ---- Combined states ----

    /// <summary>
    /// A recipe that is wrong in several ways reports each one, counts them, and does not let a recommendation
    /// masquerade as a blocker or the reverse.
    /// </summary>
    [Fact]
    public void A_recipe_failing_several_rules_reports_each_at_its_own_severity()
    {
        var result = Evaluate(
            Ready() with
            {
                InstructionStepCount = 0,
                Description = null,
                HasHeroAsset = false,
                HeroAssetLinkId = null,
                CuisineId = null,
                IngredientLines = [Line("a knob of butter", IngredientMatchStatus.Ambiguous, ingredientId: null)],
            },
            new RecipeReadinessExternalFacts(
                new AiOutstandingSummaryServiceModel(
                    2,
                    [Guid.NewGuid()],
                    [new AiOutstandingWarningServiceModel(
                        AiWarningKind.SafetyCaution, "Check the set of the custard.", Guid.NewGuid())]),
                [new IngredientAllergenReviewServiceModel(
                    Guid.NewGuid(), "wheat flour", IngredientAllergenReviewState.NoTraitsRecorded)]));

        Assert.True(result.HasBlockers);

        var blockers = result.Findings
            .Where(finding => finding.Status is RecipeReadinessStatus.Blocker)
            .Select(finding => finding.RuleId);

        Assert.Equal(
            [
                RecipeReadinessCatalogue.InstructionsPresent,
                RecipeReadinessCatalogue.IngredientsAmbiguous,
                RecipeReadinessCatalogue.AiSafetyCautionOutstanding,
            ],
            blockers);

        var recommendations = result.Findings
            .Where(finding => finding.Status is RecipeReadinessStatus.Recommendation)
            .Select(finding => finding.RuleId);

        Assert.Equal(
            [
                RecipeReadinessCatalogue.DescriptionPresent,
                RecipeReadinessCatalogue.AiChangesPending,
                RecipeReadinessCatalogue.AllergensTraitUnreviewed,

                // In the catalogue's own order, which puts media before the publication-facing rules.
                RecipeReadinessCatalogue.MediaHeroMissing,
                RecipeReadinessCatalogue.PublicationCuisineMissing,
            ],
            recommendations);

        Assert.Equal(3, result.BlockerCount);
        Assert.Equal(5, result.RecommendationCount);
    }

    /// <summary>
    /// The emptiest recipe a row can describe. It produces blockers rather than an exception, which is what a
    /// creator who has just started typing must see.
    /// </summary>
    [Fact]
    public void A_bare_recipe_is_evaluated_rather_than_refused()
    {
        var result = Evaluate(Bare(), RecipeReadinessExternalFacts.None);

        Assert.True(result.HasBlockers);
        Assert.Equal(RecipeReadinessCatalogue.Rules.Count, result.Findings.Count);
        Assert.Null(result.EvaluatedVersionId);
        Assert.Null(result.EvaluatedVersionNumber);

        // No version to have tested, and the rule says that rather than naming a version it does not have.
        Assert.Contains("no version", Blocker(result, RecipeReadinessCatalogue.TestingCurrentVersionUntested).Detail);
    }

    /// <summary>Counts add up to the findings, so a screen reading the summary and the list cannot disagree.</summary>
    [Fact]
    public void The_counts_agree_with_the_findings()
    {
        var result = Evaluate(Bare(), RecipeReadinessExternalFacts.None);

        Assert.Equal(
            result.Findings.Count(finding => finding.Status is RecipeReadinessStatus.Blocker),
            result.BlockerCount);
        Assert.Equal(
            result.Findings.Count(finding => finding.Status is RecipeReadinessStatus.Recommendation),
            result.RecommendationCount);
        Assert.Equal(result.BlockerCount > 0, result.HasBlockers);
    }

    // ---- Configuration ----

    /// <summary>
    /// A relaxed bar still appears and still says it was relaxed. Silently downgrading one would make an approval
    /// look like it cleared something it did not.
    /// </summary>
    [Fact]
    public void A_severity_override_changes_the_status_and_says_so()
    {
        // Relaxing a real blocker, so the test says something: the yield rule bars approval by default.
        var options = new RecipeReadinessOptions();
        options.Severities[RecipeReadinessCatalogue.YieldStated] = RecipeReadinessSeverity.Recommendation;

        var result = Evaluate(
            Ready() with { YieldText = null, YieldQuantity = null, YieldUnitId = null, ServingCount = null },
            RecipeReadinessExternalFacts.None,
            options);

        var finding = Recommendation(result, RecipeReadinessCatalogue.YieldStated);

        Assert.True(finding.SeverityOverridden);
        Assert.False(result.HasBlockers);
    }

    [Fact]
    public void A_rule_left_at_its_default_is_not_marked_as_overridden()
    {
        var result = Evaluate(
            Ready() with { YieldText = null, YieldQuantity = null, YieldUnitId = null, ServingCount = null });

        Assert.False(Blocker(result, RecipeReadinessCatalogue.YieldStated).SeverityOverridden);
    }

    /// <summary>
    /// Tightening is as available as relaxing, and reported the same way: an operator may decide a recommendation is
    /// a bar for their release.
    /// </summary>
    [Fact]
    public void A_recommendation_can_be_configured_as_a_blocker()
    {
        var options = new RecipeReadinessOptions();
        options.Severities[RecipeReadinessCatalogue.PublicationTagsMissing] = RecipeReadinessSeverity.Blocker;

        var result = Evaluate(Ready() with { TagCount = 0 }, RecipeReadinessExternalFacts.None, options);

        Assert.True(Blocker(result, RecipeReadinessCatalogue.PublicationTagsMissing).SeverityOverridden);
        Assert.True(result.HasBlockers);
    }

    /// <summary>
    /// A disabled rule is absent, not satisfied: it did not run, so it has not passed — and the result says which
    /// ids are missing so the shorter checklist is explained rather than mysterious.
    /// </summary>
    [Fact]
    public void A_disabled_rule_is_absent_rather_than_satisfied()
    {
        var options = new RecipeReadinessOptions();
        options.DisabledRules.Add(RecipeReadinessCatalogue.MediaHeroMissing);

        var result = Evaluate(
            Ready() with { HasHeroAsset = false, HeroAssetLinkId = null },
            RecipeReadinessExternalFacts.None,
            options);

        Assert.DoesNotContain(
            RecipeReadinessCatalogue.MediaHeroMissing, result.Findings.Select(finding => finding.RuleId));
        Assert.Equal([RecipeReadinessCatalogue.MediaHeroMissing], result.DisabledRuleIds);
        Assert.False(result.HasBlockers);
    }

    /// <summary>
    /// A stale or mistyped id must not stop an evaluation, and must not vanish either — a typo nobody can see is an
    /// override that silently never applied.
    /// </summary>
    [Fact]
    public void An_unknown_configured_rule_id_is_reported_rather_than_thrown()
    {
        var options = new RecipeReadinessOptions();
        options.DisabledRules.Add("recipe.nutrition.mapped");
        options.Severities["recipe.yield.STATED"] = RecipeReadinessSeverity.Recommendation;

        var result = Evaluate(Ready(), RecipeReadinessExternalFacts.None, options);

        Assert.Equal(["recipe.nutrition.mapped", "recipe.yield.STATED"], result.UnknownConfiguredRuleIds);
        Assert.Empty(result.DisabledRuleIds);

        // Ordinal comparison, so the miscased id overrode nothing and the real rule kept its default.
        Assert.False(Finding(result, RecipeReadinessCatalogue.YieldStated).SeverityOverridden);
    }

    [Fact]
    public void No_options_at_all_runs_every_rule_at_its_default()
    {
        var result = RecipeReadinessEvaluator.Evaluate(Ready(), RecipeReadinessExternalFacts.None);

        Assert.Equal(RecipeReadinessCatalogue.Rules.Count, result.Findings.Count);
        Assert.Empty(result.DisabledRuleIds);
        Assert.Empty(result.UnknownConfiguredRuleIds);
        Assert.All(result.Findings, finding => Assert.False(finding.SeverityOverridden));
    }

    // ---- Helpers ----

    private static RecipeReadinessServiceModel Evaluate(
        RecipeReadinessFacts facts,
        RecipeReadinessExternalFacts? external = null,
        RecipeReadinessOptions? options = null) =>
        RecipeReadinessEvaluator.Evaluate(facts, external ?? RecipeReadinessExternalFacts.None, options);

    private static RecipeReadinessFinding Finding(RecipeReadinessServiceModel result, string ruleId) =>
        Assert.Single(result.Findings, finding => finding.RuleId == ruleId);

    /// <summary>The rule fired as a blocker, and said something specific about why.</summary>
    private static RecipeReadinessFinding Blocker(RecipeReadinessServiceModel result, string ruleId)
    {
        var finding = Finding(result, ruleId);

        Assert.Equal(RecipeReadinessStatus.Blocker, finding.Status);

        // TESTRUN-004's explainability requirement, asserted rather than trusted: an unmet rule names what caused
        // it, in words and in evidence.
        Assert.NotNull(finding.Detail);
        Assert.NotEmpty(finding.Evidence);

        return finding;
    }

    private static RecipeReadinessFinding Recommendation(RecipeReadinessServiceModel result, string ruleId)
    {
        var finding = Finding(result, ruleId);

        Assert.Equal(RecipeReadinessStatus.Recommendation, finding.Status);
        Assert.NotNull(finding.Detail);
        Assert.NotEmpty(finding.Evidence);

        return finding;
    }

    private static void Satisfied(RecipeReadinessServiceModel result, string ruleId)
    {
        var finding = Finding(result, ruleId);

        Assert.Equal(RecipeReadinessStatus.Satisfied, finding.Status);

        // A satisfied rule says nothing extra: its own summary already covers it, and a detail here would be
        // prose nobody reads about a thing that is fine.
        Assert.Null(finding.Detail);
        Assert.Empty(finding.Evidence);
    }

    private static RecipeReadinessIngredientLine Line(
        string displayText, IngredientMatchStatus status, Guid? ingredientId = null) =>
        new(Guid.NewGuid(), displayText, status, ingredientId ?? Guid.NewGuid());

    /// <summary>A recipe that satisfies every rule. Each test breaks exactly one thing about it.</summary>
    private static RecipeReadinessFacts Ready() => new()
    {
        RecipeId = RecipeId,
        RecipeRowVersion = [1, 2, 3, 4, 5, 6, 7, 8],
        EvaluatedVersionId = VersionId,
        EvaluatedVersionNumber = 4,
        Title = "Olive oil cake",
        Description = "A plain, forgiving cake.",
        AttributionText = null,
        SourceUrl = null,
        CuisineId = Guid.NewGuid(),
        CourseId = Guid.NewGuid(),
        TagCount = 2,
        PrepTimeMinutes = 20,
        CookTimeMinutes = 35,
        RestTimeMinutes = null,
        TotalTimeMinutes = 55,
        YieldText = "makes 12 muffins",
        YieldQuantity = 12m,
        YieldUnitId = Guid.NewGuid(),
        ServingCount = 12m,
        InstructionStepCount = 6,
        HasHeroAsset = true,
        HeroAssetLinkId = Guid.NewGuid(),
        TestedCurrentVersion = true,
        LatestTestOutcome = TestRunOutcome.Succeeded,
        LatestTestRunId = TestRunId,
        IngredientLines = [Line("200g plain flour", IngredientMatchStatus.Matched)],
        OpenIssues = [],
    };

    /// <summary>A recipe with nothing filled in and no version — a creator who has just typed a title.</summary>
    private static RecipeReadinessFacts Bare() => new()
    {
        RecipeId = RecipeId,
        RecipeRowVersion = [1, 2, 3, 4, 5, 6, 7, 8],
        EvaluatedVersionId = null,
        EvaluatedVersionNumber = null,
        Title = "Untitled",
        Description = null,
        AttributionText = null,
        SourceUrl = null,
        CuisineId = null,
        CourseId = null,
        TagCount = 0,
        PrepTimeMinutes = null,
        CookTimeMinutes = null,
        RestTimeMinutes = null,
        TotalTimeMinutes = null,
        YieldText = null,
        YieldQuantity = null,
        YieldUnitId = null,
        ServingCount = null,
        InstructionStepCount = 0,
        HasHeroAsset = false,
        HeroAssetLinkId = null,
        TestedCurrentVersion = false,
        LatestTestOutcome = null,
        LatestTestRunId = null,
        IngredientLines = [],
        OpenIssues = [],
    };
}

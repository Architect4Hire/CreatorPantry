using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Ingredients.Data.Entities;
using CreatorPantry.Domain.Modules.Ingredients.Managers;
using CreatorPantry.Domain.Modules.Recipes.Data;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Recipes;

/// <summary>
/// The readiness fact-gathering read against a real SQL Server, and the workspace boundary it depends on.
/// </summary>
/// <remarks>
/// <para>
/// The evaluator's own tests state facts directly, which is what makes twenty rules cheap to cover. This class
/// covers the other half: that the facts the rules judge are the facts the database holds, that the counts and
/// subqueries translate, and that another workspace's recipe is not visible to any of it.
/// </para>
/// <para>
/// Deleting is done in SQL because <c>RecipeVersion</c> and <c>TestIssueResolution</c> are immutable records and
/// <c>ImmutableRecordInterceptor</c> refuses to delete one through the change tracker.
/// </para>
/// </remarks>
public sealed class RecipeReadinessRepositoryTests(SqlServerRecipeFixture fixture)
    : IClassFixture<SqlServerRecipeFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = SqlServerRecipeFixture.Now;

    public async ValueTask InitializeAsync()
    {
        await using var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);

        await SqlServerRecipeFixture.Db(scope).Database.ExecuteSqlRawAsync(
            """
            DELETE FROM TestIssueResolutions;
            DELETE FROM TestIssues;
            DELETE FROM TestObservations;
            DELETE FROM TestAttachmentLinks;
            DELETE FROM RecipeTestRuns;
            DELETE FROM RecipeVersionSnapshots;
            DELETE FROM RecipeVersions;
            DELETE FROM Recipes;
            DELETE FROM IngredientAllergenTraits;
            """,
            TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // ---- What the read finds ----

    [Fact]
    public async Task A_recipe_that_is_not_there_has_no_facts()
    {
        Assert.Null(await FindAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task The_facts_carry_the_recipes_own_columns()
    {
        var seeded = await SeedAsync(recipe =>
        {
            recipe.Description = "A plain, forgiving cake.";
            recipe.SourceUrl = "https://example.com/cake";
            recipe.AttributionText = "Adapted from Example Kitchen.";
            recipe.PrepTimeMinutes = 20;
            recipe.TotalTimeMinutes = 55;
            recipe.YieldText = "makes 12 muffins";
            recipe.ServingCount = 12m;
        });

        var facts = await FindAsync(seeded.RecipeId);

        Assert.NotNull(facts);
        Assert.Equal("A plain, forgiving cake.", facts.Description);
        Assert.Equal("https://example.com/cake", facts.SourceUrl);
        Assert.Equal("Adapted from Example Kitchen.", facts.AttributionText);
        Assert.Equal(20, facts.PrepTimeMinutes);
        Assert.Equal(55, facts.TotalTimeMinutes);
        Assert.Equal("makes 12 muffins", facts.YieldText);
        Assert.Equal(12m, facts.ServingCount);
        Assert.NotEmpty(facts.RecipeRowVersion);
    }

    /// <summary>
    /// The version the facts belong to is the latest one, by number rather than by timestamp — the number is the
    /// gap-free identity creators cite, and two versions written in the same tick would make a timestamp ordering
    /// arbitrary.
    /// </summary>
    [Fact]
    public async Task The_facts_name_the_latest_version_by_number()
    {
        var seeded = await SeedAsync(versionCount: 3);

        var facts = await FindAsync(seeded.RecipeId);

        Assert.Equal(3, facts!.EvaluatedVersionNumber);
        Assert.Equal(seeded.VersionIds[3], facts.EvaluatedVersionId);
    }

    [Fact]
    public async Task The_children_are_counted_rather_than_loaded()
    {
        // SqlServerRecipeFixture.NewRecipe seeds two ingredient groups of three lines, two instruction groups of
        // two steps, two equipment rows, two asset links and one tag.
        var seeded = await SeedAsync(populated: true);

        var facts = await FindAsync(seeded.RecipeId);

        Assert.Equal(6, facts!.IngredientLines.Count);
        Assert.Equal(4, facts.InstructionStepCount);
        Assert.Equal(1, facts.TagCount);
        Assert.True(facts.HasHeroAsset);
        Assert.NotNull(facts.HeroAssetLinkId);
    }

    [Fact]
    public async Task A_recipe_with_no_hero_link_reports_none()
    {
        var seeded = await SeedAsync();

        var facts = await FindAsync(seeded.RecipeId);

        Assert.False(facts!.HasHeroAsset);
        Assert.Null(facts.HeroAssetLinkId);
    }

    /// <summary>
    /// A gallery link is not a hero link. Without the role predicate this rule would pass on any attached image.
    /// </summary>
    [Fact]
    public async Task A_gallery_link_does_not_satisfy_the_hero_question()
    {
        var seeded = await SeedAsync(assetRoles: [RecipeAssetRole.Gallery, RecipeAssetRole.Process]);

        Assert.False((await FindAsync(seeded.RecipeId))!.HasHeroAsset);
    }

    [Fact]
    public async Task Ingredient_lines_carry_the_creators_text_and_their_match_state()
    {
        var seeded = await SeedAsync(lines:
        [
            ("200g plain flour", IngredientMatchStatus.Matched),
            ("a knob of butter", IngredientMatchStatus.Ambiguous),
        ]);

        var facts = await FindAsync(seeded.RecipeId);

        var matched = facts!.IngredientLines.Single(line => line.MatchStatus is IngredientMatchStatus.Matched);
        var ambiguous = facts.IngredientLines.Single(line => line.MatchStatus is IngredientMatchStatus.Ambiguous);

        Assert.Equal("200g plain flour", matched.DisplayText);
        Assert.Equal(SqlServerRecipeFixture.FlourId, matched.IngredientId);

        Assert.Equal("a knob of butter", ambiguous.DisplayText);
        Assert.Null(ambiguous.IngredientId);
    }

    // ---- Testing facts ----

    [Fact]
    public async Task A_recipe_nobody_has_tested_reports_the_current_version_untested()
    {
        var seeded = await SeedAsync();

        var facts = await FindAsync(seeded.RecipeId);

        Assert.False(facts!.TestedCurrentVersion);
        Assert.Null(facts.LatestTestOutcome);
        Assert.Null(facts.LatestTestRunId);
        Assert.Empty(facts.OpenIssues);
    }

    /// <summary>
    /// A test of an earlier version does not make the current one tested. This is the fact the whole test-coverage
    /// rule turns on: a test is evidence about the words that were cooked.
    /// </summary>
    [Fact]
    public async Task A_test_of_an_earlier_version_leaves_the_current_one_untested()
    {
        var seeded = await SeedAsync(versionCount: 2);
        await AddRunAsync(seeded, versionNumber: 1);

        var facts = await FindAsync(seeded.RecipeId);

        Assert.False(facts!.TestedCurrentVersion);
        Assert.Null(facts.LatestTestOutcome);
    }

    [Fact]
    public async Task A_test_of_the_current_version_is_found_with_its_outcome()
    {
        var seeded = await SeedAsync(versionCount: 2);
        await AddRunAsync(seeded, versionNumber: 2, outcome: TestRunOutcome.SucceededWithIssues);

        var facts = await FindAsync(seeded.RecipeId);

        Assert.True(facts!.TestedCurrentVersion);
        Assert.Equal(TestRunOutcome.SucceededWithIssues, facts.LatestTestOutcome);
    }

    /// <summary>
    /// The most recently <em>cooked</em> test, not the most recently entered — the same ordering the test history
    /// uses, because testers write their notes up days later.
    /// </summary>
    [Fact]
    public async Task The_latest_outcome_reads_the_test_cooked_last_not_entered_last()
    {
        var seeded = await SeedAsync();

        await AddRunAsync(
            seeded, versionNumber: 1, outcome: TestRunOutcome.Failed, testedAt: Now, createdAt: Now.AddDays(-10));
        await AddRunAsync(
            seeded,
            versionNumber: 1,
            outcome: TestRunOutcome.Succeeded,
            testedAt: Now.AddDays(-5),
            createdAt: Now.AddDays(5));

        // Cooked most recently: the failure. Entered most recently: the success.
        Assert.Equal(TestRunOutcome.Failed, (await FindAsync(seeded.RecipeId))!.LatestTestOutcome);
    }

    /// <summary>
    /// "Unresolved" is the absence of a resolution row and nothing else, which is the one meaning
    /// <c>TestIssue</c> establishes. A resolved issue is simply not here.
    /// </summary>
    [Fact]
    public async Task Only_unresolved_issues_of_the_current_version_are_gathered()
    {
        var seeded = await SeedAsync();

        await AddRunAsync(seeded, versionNumber: 1, issues:
        [
            ("Collapsed in the tin", TestIssueSeverity.Blocking, false),
            ("Too salty", TestIssueSeverity.Major, true),
            ("Untidy edges", TestIssueSeverity.Minor, false),
        ]);

        var facts = await FindAsync(seeded.RecipeId);

        Assert.Equal(
            ["Collapsed in the tin", "Untidy edges"],
            facts!.OpenIssues.Select(issue => issue.Title).Order());
        Assert.Equal(
            TestIssueSeverity.Blocking,
            facts.OpenIssues.Single(issue => issue.Title == "Collapsed in the tin").Severity);
    }

    /// <summary>
    /// An unresolved issue found by a test of an earlier version is not the current version's problem. Without the
    /// version predicate, a recipe could never clear an issue except by resolving history.
    /// </summary>
    [Fact]
    public async Task An_unresolved_issue_from_an_earlier_version_is_not_gathered()
    {
        var seeded = await SeedAsync(versionCount: 2);

        await AddRunAsync(seeded, versionNumber: 1, issues: [("Collapsed in the tin", TestIssueSeverity.Blocking, false)]);

        Assert.Empty((await FindAsync(seeded.RecipeId))!.OpenIssues);
    }

    // ---- End to end through the evaluator ----

    /// <summary>
    /// The read and the rules together, on a recipe that is genuinely incomplete. Proof that the facts the database
    /// produces are the shape the evaluator judges — a mismatch between them would pass both halves' own tests.
    /// </summary>
    [Fact]
    public async Task A_seeded_incomplete_recipe_evaluates_to_the_expected_blockers()
    {
        var seeded = await SeedAsync(lines: [("a knob of butter", IngredientMatchStatus.Ambiguous)]);

        var facts = await FindAsync(seeded.RecipeId);
        var result = RecipeReadinessEvaluator.Evaluate(facts!, RecipeReadinessExternalFacts.None);

        var blockers = result.Findings
            .Where(finding => finding.Status is RecipeReadinessStatus.Blocker)
            .Select(finding => finding.RuleId);

        Assert.Equal(
            [
                RecipeReadinessCatalogue.InstructionsPresent,
                RecipeReadinessCatalogue.YieldStated,
                RecipeReadinessCatalogue.TimeStated,
                RecipeReadinessCatalogue.IngredientsAmbiguous,
                RecipeReadinessCatalogue.TestingCurrentVersionUntested,
                RecipeReadinessCatalogue.MediaHeroMissing,
            ],
            blockers);

        Assert.Equal(seeded.VersionIds[1], result.EvaluatedVersionId);
        Assert.Equal(RecipeConcurrencyToken.From(facts!.RecipeRowVersion), result.ConcurrencyToken);
    }

    // ---- Workspace isolation ----

    /// <summary>
    /// Another workspace's recipe has no facts at all — not a partially filled record, and not an error naming it.
    /// The read cannot see it, which is what lets the seam above answer 404 without disclosing that it is real.
    /// </summary>
    [Fact]
    public async Task Another_workspaces_recipe_has_no_facts()
    {
        var inA = await SeedAsync(populated: true);

        Assert.Null(await FindAsync(inA.RecipeId, SqlServerRecipeFixture.WorkspaceB));
    }

    /// <summary>
    /// Both workspaces hold a recipe with the same title, both tested on the same day, and each reads only its own.
    /// So isolation cannot pass by the two being distinguishable.
    /// </summary>
    [Fact]
    public async Task Each_workspace_gathers_only_its_own_facts()
    {
        var inA = await SeedAsync(versionCount: 1);
        await AddRunAsync(inA, versionNumber: 1, outcome: TestRunOutcome.Failed);

        var inB = await SeedAsync(versionCount: 1, workspaceId: SqlServerRecipeFixture.WorkspaceB);
        await AddRunAsync(
            inB,
            versionNumber: 1,
            outcome: TestRunOutcome.Succeeded,
            issues: [("B's own problem", TestIssueSeverity.Blocking, false)],
            workspaceId: SqlServerRecipeFixture.WorkspaceB);

        var factsA = await FindAsync(inA.RecipeId);
        var factsB = await FindAsync(inB.RecipeId, SqlServerRecipeFixture.WorkspaceB);

        Assert.Equal(TestRunOutcome.Failed, factsA!.LatestTestOutcome);
        Assert.Empty(factsA.OpenIssues);

        Assert.Equal(TestRunOutcome.Succeeded, factsB!.LatestTestOutcome);
        Assert.Equal("B's own problem", Assert.Single(factsB.OpenIssues).Title);
    }

    // ---- The allergen read, which is global reference data ----

    /// <summary>
    /// The allergen gaps read is over shared reference data, so both workspaces get the same answer — which is
    /// correct and worth pinning: an ingredient's allergen records are not a creator's property, and duplicating
    /// them per workspace is what tenancy.md forbids.
    /// </summary>
    [Fact]
    public async Task An_ingredient_with_no_recorded_traits_is_a_gap_in_either_workspace()
    {
        foreach (var workspaceId in (Guid[])[SqlServerRecipeFixture.WorkspaceA, SqlServerRecipeFixture.WorkspaceB])
        {
            var gaps = await AllergenGapsAsync([SqlServerRecipeFixture.FlourId], workspaceId);

            var gap = Assert.Single(gaps);
            Assert.Equal(SqlServerRecipeFixture.FlourId, gap.IngredientId);
            Assert.Equal(IngredientAllergenReviewState.NoTraitsRecorded, gap.State);
        }
    }

    /// <summary>
    /// An id the catalogue will not vouch for is reported as a gap rather than dropped: silence would read as
    /// "checked", and inferring a clean allergen picture from a missing ingredient is exactly the inference
    /// recipes.md forbids.
    /// </summary>
    [Fact]
    public async Task An_unknown_ingredient_id_is_reported_as_a_gap_rather_than_dropped()
    {
        var unknown = Guid.NewGuid();

        var gap = Assert.Single(await AllergenGapsAsync([unknown]));

        Assert.Equal(unknown, gap.IngredientId);
        Assert.Equal(IngredientAllergenReviewState.NoTraitsRecorded, gap.State);
    }

    [Theory]
    [InlineData(TraitReviewStatus.Unreviewed, AllergenPresence.Present, IngredientAllergenReviewState.AwaitingReview)]
    [InlineData(TraitReviewStatus.Approved, AllergenPresence.Unknown, IngredientAllergenReviewState.PresenceUncertain)]
    [InlineData(TraitReviewStatus.Approved, AllergenPresence.PossiblePresence, IngredientAllergenReviewState.PresenceUncertain)]
    public async Task A_recorded_trait_produces_the_state_its_review_and_presence_describe(
        TraitReviewStatus reviewStatus,
        AllergenPresence presence,
        IngredientAllergenReviewState expected)
    {
        await AddTraitAsync(reviewStatus, presence);

        Assert.Equal(expected, Assert.Single(await AllergenGapsAsync([SqlServerRecipeFixture.FlourId])).State);
    }

    /// <summary>A reviewed, settled trait is the one case that is not a gap, and it is still only about records.</summary>
    [Theory]
    [InlineData(AllergenPresence.Present)]
    [InlineData(AllergenPresence.NotListedBySource)]
    public async Task A_reviewed_settled_trait_is_not_a_gap(AllergenPresence presence)
    {
        await AddTraitAsync(TraitReviewStatus.Approved, presence);

        Assert.Empty(await AllergenGapsAsync([SqlServerRecipeFixture.FlourId]));
    }

    /// <summary>
    /// A withdrawn claim is not a claim. An ingredient whose only trait was rejected has nothing recorded, which is
    /// a gap rather than a pass.
    /// </summary>
    [Theory]
    [InlineData(TraitReviewStatus.Rejected)]
    [InlineData(TraitReviewStatus.Superseded)]
    public async Task A_rejected_or_superseded_trait_counts_for_nothing(TraitReviewStatus reviewStatus)
    {
        await AddTraitAsync(reviewStatus, AllergenPresence.NotListedBySource);

        Assert.Equal(
            IngredientAllergenReviewState.NoTraitsRecorded,
            Assert.Single(await AllergenGapsAsync([SqlServerRecipeFixture.FlourId])).State);
    }

    /// <summary>
    /// Nothing recorded outranks a claim awaiting review, which outranks a reviewed claim that is unsettled — the
    /// precedence stated once, asserted here where two traits apply at the same time.
    /// </summary>
    [Fact]
    public async Task An_unreviewed_trait_outranks_a_reviewed_but_uncertain_one()
    {
        await AddTraitAsync(TraitReviewStatus.Approved, AllergenPresence.Unknown, effectiveYear: 2025);
        await AddTraitAsync(TraitReviewStatus.Unreviewed, AllergenPresence.Present, effectiveYear: 2026);

        Assert.Equal(
            IngredientAllergenReviewState.AwaitingReview,
            Assert.Single(await AllergenGapsAsync([SqlServerRecipeFixture.FlourId])).State);
    }

    [Fact]
    public async Task No_ingredients_asked_about_reads_nothing()
    {
        Assert.Empty(await AllergenGapsAsync([]));
    }

    // ---- Helpers ----

    private async Task<RecipeReadinessFacts?> FindAsync(Guid recipeId, Guid? workspaceId = null)
    {
        await using var scope = fixture.ScopeFor(workspaceId ?? SqlServerRecipeFixture.WorkspaceA);

        return await scope.ServiceProvider.GetRequiredService<IRecipeReadinessRepository>()
            .FindFactsAsync(recipeId, TestContext.Current.CancellationToken);
    }

    private async Task<IReadOnlyList<IngredientAllergenReviewServiceModel>> AllergenGapsAsync(
        IReadOnlyCollection<Guid> ingredientIds,
        Guid? workspaceId = null)
    {
        await using var scope = fixture.ScopeFor(workspaceId ?? SqlServerRecipeFixture.WorkspaceA);

        return await scope.ServiceProvider
            .GetRequiredService<Domain.Modules.Ingredients.Business.IIngredientBusiness>()
            .FindAllergenReviewGapsAsync(ingredientIds, TestContext.Current.CancellationToken);
    }

    private async Task AddTraitAsync(
        TraitReviewStatus reviewStatus, AllergenPresence presence, int effectiveYear = 2026)
    {
        await using var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);
        var db = SqlServerRecipeFixture.Db(scope);

        db.Set<IngredientAllergenTrait>().Add(new IngredientAllergenTrait
        {
            Id = Guid.NewGuid(),
            IngredientId = SqlServerRecipeFixture.FlourId,
            AllergenId = SqlServerRecipeFixture.AllergenId,
            ReferenceSourceId = SqlServerRecipeFixture.ReferenceSourceId,
            ReferenceSourceKind = ReferenceSourceKind.OfficialDatabase,
            Presence = presence,
            EvidenceNote = "seeded for a test",
            // Part of UX_IngredientAllergenTraits_Ingredient_Allergen_Source_Effective, so two traits for one
            // ingredient need distinct dates — which is the model recording a claim superseding another.
            EffectiveFrom = new DateOnly(effectiveYear, 1, 1),
            ReviewStatus = reviewStatus,
        });

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();
    }

    private sealed record SeededRecipe(Guid RecipeId, Dictionary<int, Guid> VersionIds);

    /// <summary>
    /// Seeds one recipe with the parts a test asked for.
    /// </summary>
    /// <remarks>
    /// <c>WorkspaceId</c> is never set on anything — the ownership interceptor stamps it from the resolved context,
    /// and feature code assigning it is a defect.
    /// </remarks>
    private async Task<SeededRecipe> SeedAsync(
        Action<Recipe>? configure = null,
        int versionCount = 1,
        bool populated = false,
        Guid? workspaceId = null,
        IReadOnlyList<(string Text, IngredientMatchStatus Status)>? lines = null,
        IReadOnlyList<RecipeAssetRole>? assetRoles = null)
    {
        await using var scope = fixture.ScopeFor(workspaceId ?? SqlServerRecipeFixture.WorkspaceA);
        var db = SqlServerRecipeFixture.Db(scope);

        var tagId = workspaceId == SqlServerRecipeFixture.WorkspaceB
            ? SqlServerRecipeFixture.TagIdB
            : SqlServerRecipeFixture.TagIdA;

        Recipe recipe;

        if (populated)
        {
            recipe = SqlServerRecipeFixture.NewRecipe("Olive oil cake", tagId);
        }
        else
        {
            recipe = new Recipe
            {
                Id = Guid.NewGuid(),
                Title = "Olive oil cake",
                Status = RecipeStatus.Draft,
                CreatedByMembershipId = SqlServerRecipeFixture.AuthorOne,
                UpdatedByMembershipId = SqlServerRecipeFixture.AuthorOne,
                CreatedAt = Now,
                UpdatedAt = Now,
            };

            if (lines is { Count: > 0 })
            {
                var group = new RecipeIngredientGroup
                {
                    Id = Guid.NewGuid(),
                    RecipeId = recipe.Id,
                    SortOrder = 0,
                };

                for (var index = 0; index < lines.Count; index++)
                {
                    var (text, status) = lines[index];
                    var matched = status is IngredientMatchStatus.Matched;

                    group.Ingredients.Add(new RecipeIngredient
                    {
                        Id = Guid.NewGuid(),
                        RecipeId = recipe.Id,
                        RecipeIngredientGroupId = group.Id,
                        SortOrder = index,
                        DisplayText = text,

                        // CK_RecipeIngredients_Match_Status: matched means matched to something, and anything else
                        // means matched to nothing.
                        IngredientId = matched ? SqlServerRecipeFixture.FlourId : null,
                        MatchStatus = status,
                    });
                }

                recipe.IngredientGroups.Add(group);
            }

            for (var index = 0; index < (assetRoles?.Count ?? 0); index++)
            {
                recipe.AssetLinks.Add(new RecipeAssetLink
                {
                    Id = Guid.NewGuid(),
                    RecipeId = recipe.Id,
                    SortOrder = index,
                    MediaAssetId = Guid.NewGuid(),
                    Role = assetRoles![index],
                });
            }
        }

        configure?.Invoke(recipe);

        db.Recipes.Add(recipe);

        var versionIds = new Dictionary<int, Guid>();

        for (var number = 1; number <= versionCount; number++)
        {
            var version = new RecipeVersion
            {
                Id = Guid.NewGuid(),
                RecipeId = recipe.Id,
                VersionNumber = number,
                Source = RecipeVersionSource.CreatorEdit,
                Readiness = RecipeVersionReadiness.Draft,
                CreatedByMembershipId = SqlServerRecipeFixture.AuthorOne,
                CreatedAt = Now.AddDays(-versionCount + number),
                SnapshotSchemaVersion = 1,
            };

            db.RecipeVersions.Add(version);
            versionIds[number] = version.Id;
        }

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        return new SeededRecipe(recipe.Id, versionIds);
    }

    /// <summary>
    /// Records one test against one of the recipe's versions. <c>issues</c> is one entry per issue, the boolean
    /// meaning it has been resolved.
    /// </summary>
    private async Task AddRunAsync(
        SeededRecipe seeded,
        int versionNumber,
        TestRunOutcome outcome = TestRunOutcome.Succeeded,
        DateTimeOffset? testedAt = null,
        DateTimeOffset? createdAt = null,
        IReadOnlyList<(string Title, TestIssueSeverity Severity, bool Resolved)>? issues = null,
        Guid? workspaceId = null)
    {
        await using var scope = fixture.ScopeFor(workspaceId ?? SqlServerRecipeFixture.WorkspaceA);
        var db = SqlServerRecipeFixture.Db(scope);

        var run = new RecipeTestRun
        {
            Id = Guid.NewGuid(),
            RecipeId = seeded.RecipeId,
            RecipeVersionId = seeded.VersionIds[versionNumber],
            TestedAt = testedAt ?? Now,
            TestedByMembershipId = SqlServerRecipeFixture.AuthorOne,
            Outcome = outcome,
            CreatedByMembershipId = SqlServerRecipeFixture.AuthorOne,
            UpdatedByMembershipId = SqlServerRecipeFixture.AuthorOne,
            CreatedAt = createdAt ?? testedAt ?? Now,
            UpdatedAt = createdAt ?? testedAt ?? Now,
        };

        var resolutions = new List<TestIssueResolution>();

        for (var index = 0; index < (issues?.Count ?? 0); index++)
        {
            var (title, severity, resolved) = issues![index];

            var issue = new TestIssue
            {
                Id = Guid.NewGuid(),
                RecipeId = seeded.RecipeId,
                RecipeTestRunId = run.Id,
                Severity = severity,
                Title = title,
                SortOrder = index,
            };

            run.Issues.Add(issue);

            if (resolved)
            {
                resolutions.Add(new TestIssueResolution
                {
                    Id = Guid.NewGuid(),
                    RecipeId = seeded.RecipeId,
                    TestIssueId = issue.Id,
                    Kind = TestIssueResolutionKind.WontFix,
                    ResolvedByMembershipId = SqlServerRecipeFixture.AuthorOne,
                    ResolvedAt = Now,
                });
            }
        }

        db.RecipeTestRuns.Add(run);
        db.TestIssueResolutions.AddRange(resolutions);

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();
    }
}

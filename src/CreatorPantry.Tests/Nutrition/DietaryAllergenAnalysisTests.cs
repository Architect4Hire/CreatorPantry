using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Ingredients.Data.Entities;
using CreatorPantry.Domain.Modules.Ingredients.Managers;
using CreatorPantry.Domain.Modules.Nutrition.Business;
using CreatorPantry.Domain.Modules.Nutrition.Facade;
using CreatorPantry.Domain.Modules.Nutrition.Managers;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Nutrition;

/// <summary>
/// The dietary and allergen analysis end to end (14.4): real trait rows on SQL Server, read through the Ingredients
/// and Vocabulary facades — which ones an analysis may see, as of when, and from whom.
/// </summary>
public sealed class DietaryAllergenAnalysisTests(SqlServerNutritionFixture fixture) : IClassFixture<SqlServerNutritionFixture>
{
    private static readonly Guid WorkspaceA = SqlServerNutritionFixture.WorkspaceA;
    private static readonly DateOnly Today = DateOnly.FromDateTime(SqlServerNutritionFixture.Now.UtcDateTime);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Only_approved_traits_in_effect_today_reach_an_analysis()
    {
        var approved = await IngredientAsync("approved");
        var unreviewed = await IngredientAsync("unreviewed");
        var rejected = await IngredientAsync("rejected");
        var superseded = await IngredientAsync("superseded");
        var future = await IngredientAsync("future");

        await TraitsAsync(
            Allergen(approved, AllergenPresence.Present),
            Allergen(unreviewed, AllergenPresence.Present, status: TraitReviewStatus.Unreviewed),
            Allergen(rejected, AllergenPresence.Present, status: TraitReviewStatus.Rejected),
            Allergen(superseded, AllergenPresence.Present, status: TraitReviewStatus.Superseded),
            Allergen(future, AllergenPresence.Present, effectiveFrom: Today.AddDays(1)));

        var analysis = await AnalyzeAsync([approved, unreviewed, rejected, superseded, future], allergens: [SqlServerNutritionFixture.MilkId]);

        var milk = Assert.Single(analysis.Allergens);
        Assert.Equal(RecipeAllergenFinding.Contains, milk.Finding);
        Assert.Equal(
            [AllergenLineFinding.Contains, AllergenLineFinding.Unknown, AllergenLineFinding.Unknown, AllergenLineFinding.Unknown, AllergenLineFinding.Unknown],
            milk.Lines.Select(line => line.Finding));
        Assert.Equal(Today, analysis.EvaluatedOn);
    }

    [Fact]
    public async Task A_source_revising_itself_supersedes_its_own_earlier_word_and_never_another_sources()
    {
        var ingredient = await IngredientAsync("revised");

        await TraitsAsync(
            // The official list first said present, then published a composition without it.
            Allergen(ingredient, AllergenPresence.Present, effectiveFrom: Today.AddYears(-2)),
            Allergen(ingredient, AllergenPresence.NotListedBySource, effectiveFrom: Today.AddYears(-1), note: "Reformulated; not in this listing."));

        var revised = Assert.Single(Assert.Single((await AnalyzeAsync([ingredient], allergens: [SqlServerNutritionFixture.MilkId])).Allergens).Lines);
        Assert.Equal(AllergenLineFinding.NotListed, revised.Finding);
        Assert.Equal(Today.AddYears(-1), Assert.Single(revised.Evidence).EffectiveFrom);

        // A publisher still says present: that is a disagreement, kept and reported, not a revision.
        await TraitsAsync(Allergen(ingredient, AllergenPresence.Present, source: SqlServerNutritionFixture.PublisherId, kind: ReferenceSourceKind.Publisher));

        var disputed = Assert.Single(Assert.Single((await AnalyzeAsync([ingredient], allergens: [SqlServerNutritionFixture.MilkId])).Allergens).Lines);
        Assert.Equal((AllergenLineFinding.Contains, true, 2), (disputed.Finding, disputed.Conflict, disputed.Evidence.Count));
    }

    [Fact]
    public async Task A_later_does_not_say_never_supersedes_the_same_sources_earlier_warning()
    {
        var ingredient = await IngredientAsync("unknown-later");

        await TraitsAsync(
            Allergen(ingredient, AllergenPresence.Present, effectiveFrom: Today.AddYears(-2)),
            Allergen(ingredient, AllergenPresence.Unknown, effectiveFrom: Today.AddYears(-1), note: "Consulted; not stated."));

        var line = Assert.Single(Assert.Single((await AnalyzeAsync([ingredient], allergens: [SqlServerNutritionFixture.MilkId])).Allergens).Lines);

        Assert.Equal(AllergenLineFinding.Contains, line.Finding);
        Assert.Equal(Today.AddYears(-2), Assert.Single(line.Evidence).EffectiveFrom);
    }

    [Fact]
    public async Task A_trait_effective_today_counts_and_one_effective_tomorrow_does_not()
    {
        var today = await IngredientAsync("today");
        var tomorrow = await IngredientAsync("tomorrow");

        await TraitsAsync(
            Allergen(today, AllergenPresence.Present, effectiveFrom: Today),
            Allergen(tomorrow, AllergenPresence.Present, effectiveFrom: Today.AddDays(1)));

        var milk = Assert.Single((await AnalyzeAsync([today, tomorrow], allergens: [SqlServerNutritionFixture.MilkId])).Allergens);

        Assert.Equal([AllergenLineFinding.Contains, AllergenLineFinding.Unknown], milk.Lines.Select(line => line.Finding));
    }

    [Fact]
    public async Task A_retired_sources_not_listed_is_cited_and_not_counted()
    {
        var ingredient = await IngredientAsync("retired-source");
        var retired = Guid.NewGuid();

        await using (var scope = fixture.PlatformScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
            db.ReferenceSources.Add(new ReferenceSource
            {
                Id = retired, Code = $"retired-{retired:N}"[..32], Name = "A retired list", Kind = ReferenceSourceKind.OfficialDatabase,
                Citation = "Retired.", IsActive = false,
            });
            await db.SaveChangesAsync(Ct);
        }

        await TraitsAsync(Allergen(ingredient, AllergenPresence.NotListedBySource, source: retired, note: "Not in this listing."));

        var line = Assert.Single(Assert.Single((await AnalyzeAsync([ingredient], allergens: [SqlServerNutritionFixture.MilkId])).Allergens).Lines);

        Assert.Equal(AllergenLineFinding.Unknown, line.Finding);
        Assert.False(Assert.Single(line.Evidence).Counted);
    }

    [Fact]
    public async Task More_than_twenty_allergens_at_once_is_invalid()
    {
        var model = new DietaryAllergenAnalysisViewModel { AllergenIds = [.. Enumerable.Range(0, 21).Select(_ => Guid.NewGuid())] };

        Assert.Equal(NutritionErrorCodes.AnalysisInvalid, (await Facade().AnalyzeAsync(Guid.NewGuid(), Guid.NewGuid(), model, Ct)).Error?.Code);
    }

    [Fact]
    public async Task Dietary_traits_are_read_with_the_notes_that_explain_them()
    {
        var sugar = await IngredientAsync("sugar");
        await TraitsAsync(Dietary(sugar, SqlServerNutritionFixture.VeganId, DietaryCompatibility.DependsOnProduct, "Some cane sugar is filtered through bone char."));

        var vegan = Assert.Single((await AnalyzeAsync([sugar], profiles: [SqlServerNutritionFixture.VeganId])).Dietary);

        Assert.Equal(RecipeDietaryFinding.DependsOnProduct, vegan.Finding);
        Assert.Equal("Vegan", vegan.DietaryProfileName);
        Assert.Equal("Some cane sugar is filtered through bone char.", Assert.Single(Assert.Single(vegan.Lines).Evidence).EvidenceNote);
    }

    [Fact]
    public async Task A_retired_or_unknown_allergen_or_profile_cannot_be_asked_about()
    {
        var facade = Facade();

        foreach (var model in new[]
        {
            new DietaryAllergenAnalysisViewModel { AllergenIds = [SqlServerNutritionFixture.RetiredAllergenId] },
            new DietaryAllergenAnalysisViewModel { AllergenIds = [Guid.NewGuid()] },
            new DietaryAllergenAnalysisViewModel { DietaryProfileIds = [SqlServerNutritionFixture.MilkId] },
        })
        {
            Assert.Equal(NutritionErrorCodes.AnalysisSubjectUnprocessable, (await facade.AnalyzeAsync(Guid.NewGuid(), Guid.NewGuid(), model, Ct)).Error?.Code);
        }
    }

    [Fact]
    public async Task An_analysis_of_nothing_or_of_the_same_thing_twice_is_invalid()
    {
        var facade = Facade();

        Assert.Equal(NutritionErrorCodes.AnalysisInvalid, (await facade.AnalyzeAsync(Guid.NewGuid(), Guid.NewGuid(), new(), Ct)).Error?.Code);
        Assert.Equal(
            NutritionErrorCodes.AnalysisInvalid,
            (await facade.AnalyzeAsync(Guid.NewGuid(), Guid.NewGuid(), new() { AllergenIds = [SqlServerNutritionFixture.MilkId, SqlServerNutritionFixture.MilkId] }, Ct)).Error?.Code);
    }

    [Fact]
    public async Task A_version_that_is_not_there_is_not_found()
    {
        var result = await Facade().AnalyzeAsync(Guid.NewGuid(), Guid.NewGuid(), new() { AllergenIds = [SqlServerNutritionFixture.MilkId] }, Ct);

        Assert.Equal(NutritionErrorCodes.EstimateRecipeNotFound, result.Error?.Code);
    }

    [Fact]
    public async Task A_viewer_may_read_an_analysis()
    {
        var ingredient = await IngredientAsync("viewer");

        var analysis = await AnalyzeAsync([ingredient], allergens: [SqlServerNutritionFixture.WheatId], role: WorkspaceRole.Viewer);

        Assert.Equal(RecipeAllergenFinding.Unknown, Assert.Single(analysis.Allergens).Finding);
    }

    // ---- Helpers ----

    private IDietaryAllergenAnalysisFacade Facade(WorkspaceRole role = WorkspaceRole.Editor) =>
        fixture.ScopeFor(WorkspaceA, role).ServiceProvider.GetRequiredService<IDietaryAllergenAnalysisFacade>();

    private async Task<DietaryAllergenAnalysisServiceModel> AnalyzeAsync(
        IReadOnlyList<Guid> ingredients,
        IReadOnlyList<Guid>? allergens = null,
        IReadOnlyList<Guid>? profiles = null,
        WorkspaceRole role = WorkspaceRole.Editor)
    {
        var (recipe, version) = (Guid.NewGuid(), Guid.NewGuid());
        fixture.Versions.Add(recipe, version, new NutritionRecipeVersion(
            1,
            [.. ingredients.Select((ingredient, i) => new NutritionEstimateLineInput(Guid.NewGuid(), $"line {i}", null, null, null, ingredient, false))],
            null,
            null,
            null));

        var result = await Facade(role).AnalyzeAsync(
            recipe, version, new() { AllergenIds = [.. allergens ?? []], DietaryProfileIds = [.. profiles ?? []] }, Ct);

        return Value(result);
    }

    private Task<Guid> IngredientAsync(string stem) => fixture.NewIngredientAsync($"{stem} {Guid.NewGuid():N}");

    private static IngredientAllergenTrait Allergen(
        Guid ingredient,
        AllergenPresence presence,
        TraitReviewStatus status = TraitReviewStatus.Approved,
        DateOnly? effectiveFrom = null,
        Guid? source = null,
        ReferenceSourceKind kind = ReferenceSourceKind.OfficialDatabase,
        string note = "Stated by the source.") => new()
        {
            Id = Guid.NewGuid(),
            IngredientId = ingredient,
            AllergenId = SqlServerNutritionFixture.MilkId,
            ReferenceSourceId = source ?? SqlServerNutritionFixture.OfficialListId,
            ReferenceSourceKind = kind,
            Presence = presence,
            EvidenceNote = note,
            EffectiveFrom = effectiveFrom ?? Today.AddYears(-1),
            ReviewStatus = status,
        };

    private static IngredientDietaryTrait Dietary(Guid ingredient, Guid profile, DietaryCompatibility compatibility, string note) => new()
    {
        Id = Guid.NewGuid(),
        IngredientId = ingredient,
        DietaryProfileId = profile,
        ReferenceSourceId = SqlServerNutritionFixture.OfficialListId,
        ReferenceSourceKind = ReferenceSourceKind.OfficialDatabase,
        Compatibility = compatibility,
        EvidenceNote = note,
        EffectiveFrom = Today.AddYears(-1),
        ReviewStatus = TraitReviewStatus.Approved,
    };

    private async Task TraitsAsync(params object[] traits)
    {
        await using var scope = fixture.PlatformScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        db.IngredientAllergenTraits.AddRange(traits.OfType<IngredientAllergenTrait>());
        db.IngredientDietaryTraits.AddRange(traits.OfType<IngredientDietaryTrait>());
        await db.SaveChangesAsync(Ct);
    }

    private static T Value<T>(OperationResult<T> result)
    {
        Assert.True(result.Succeeded, $"{result.Error?.Code}: {result.Error?.Message}");
        return result.Value!;
    }
}

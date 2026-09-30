using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Ai.Facade;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.Ingredients.Facade;
using CreatorPantry.Domain.Modules.Ingredients.Managers;
using CreatorPantry.Domain.Modules.Recipes.Business;
using CreatorPantry.Domain.Modules.Recipes.Facade;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CreatorPantry.Tests.Recipes;

/// <summary>
/// What the readiness facade composes: which modules it asks, what it asks them, and when it does not ask at all.
/// </summary>
/// <remarks>
/// The facade exists because four of the twenty rules read other modules' data and Business may not cross a module
/// boundary. Under test here is that arrangement — the order of the calls and their arguments — rather than any
/// rule, which <see cref="RecipeReadinessEvaluatorTests"/> covers.
/// </remarks>
public sealed class RecipeReadinessFacadeTests
{
    private static readonly Guid RecipeId = Guid.NewGuid();

    private readonly StubReadinessBusiness _business = new();
    private readonly StubProposalFacade _proposals = new();
    private readonly StubIngredientFacade _ingredients = new();
    private readonly IRecipeReadinessFacade _facade;

    public RecipeReadinessFacadeTests() =>
        _facade = new ServiceCollection()
            .AddSingleton<IRecipeReadinessBusiness>(_business)
            .AddSingleton<IAiProposalFacade>(_proposals)
            .AddSingleton<IIngredientFacade>(_ingredients)
            .AddSingleton<IRecipeReadinessFacade, RecipeReadinessFacade>()
            .BuildServiceProvider()
            .GetRequiredService<IRecipeReadinessFacade>();

    /// <summary>
    /// A recipe the caller may not see is refused before either module is asked. Two reasons, and both matter: work
    /// on behalf of somebody who gets a 404 is wasted, and asking the AI module about a recipe this caller has no
    /// business naming is a question that should never be put.
    /// </summary>
    [Fact]
    public async Task An_invisible_recipe_is_refused_without_asking_the_other_modules()
    {
        _business.Facts = null;

        var result = await _facade.EvaluateAsync(RecipeId, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(RecipeErrorCodes.RecipeNotFound, result.Error!.Code);
        Assert.Equal(0, _proposals.Calls);
        Assert.Equal(0, _ingredients.Calls);
        Assert.Equal(0, _business.Evaluations);
    }

    /// <summary>
    /// The allergen question is asked about the vocabulary entries the lines turned out to carry — which is why the
    /// evaluation is split in two: nothing could have known these ids before the facts were read.
    /// </summary>
    [Fact]
    public async Task The_allergen_read_is_asked_about_the_matched_ingredients_only()
    {
        var flour = Guid.NewGuid();
        var butter = Guid.NewGuid();

        _business.Facts = Facts(
        [
            new RecipeReadinessIngredientLine(Guid.NewGuid(), "200g flour", IngredientMatchStatus.Matched, flour),
            new RecipeReadinessIngredientLine(Guid.NewGuid(), "60g butter", IngredientMatchStatus.Matched, butter),
            new RecipeReadinessIngredientLine(Guid.NewGuid(), "a pinch", IngredientMatchStatus.NoMatch, null),
        ]);

        await _facade.EvaluateAsync(RecipeId, TestContext.Current.CancellationToken);

        // Line order, which the facade preserves: it selects the ids off the lines as they come. Sorting instead
        // would make the assertion depend on how two fresh Guids happen to compare, which is a fact about those
        // Guids and not about the facade.
        Assert.Equal([flour, butter], _ingredients.AskedAbout);
    }

    /// <summary>
    /// Two lines resolving to one ingredient ask about it once. A duplicated id would be a second row in the answer
    /// and a second finding about one ingredient.
    /// </summary>
    [Fact]
    public async Task A_repeated_ingredient_is_asked_about_once()
    {
        var flour = Guid.NewGuid();

        _business.Facts = Facts(
        [
            new RecipeReadinessIngredientLine(Guid.NewGuid(), "200g flour", IngredientMatchStatus.Matched, flour),
            new RecipeReadinessIngredientLine(Guid.NewGuid(), "50g more flour", IngredientMatchStatus.Matched, flour),
        ]);

        await _facade.EvaluateAsync(RecipeId, TestContext.Current.CancellationToken);

        Assert.Equal([flour], _ingredients.AskedAbout);
    }

    /// <summary>
    /// No matched line means no allergen read at all: a read of nothing is a read nobody needs, and the
    /// unrecognised-lines rule already reports that situation in its own words.
    /// </summary>
    [Fact]
    public async Task A_recipe_with_no_matched_lines_skips_the_allergen_read_entirely()
    {
        _business.Facts = Facts(
            [new RecipeReadinessIngredientLine(Guid.NewGuid(), "a pinch", IngredientMatchStatus.NoMatch, null)]);

        await _facade.EvaluateAsync(RecipeId, TestContext.Current.CancellationToken);

        Assert.Equal(0, _ingredients.Calls);

        // The AI module is still asked: its question is about the recipe, not about its ingredients.
        Assert.Equal(1, _proposals.Calls);
    }

    /// <summary>
    /// What both modules said reaches the evaluation. Without this the facade could gather facts and quietly drop
    /// them, and every rule that depends on them would report satisfied.
    /// </summary>
    [Fact]
    public async Task Both_modules_answers_are_handed_to_the_evaluation()
    {
        var flour = Guid.NewGuid();
        var proposalId = Guid.NewGuid();

        _business.Facts = Facts(
            [new RecipeReadinessIngredientLine(Guid.NewGuid(), "200g flour", IngredientMatchStatus.Matched, flour)]);

        _proposals.Summary = new AiOutstandingSummaryServiceModel(
            2,
            [proposalId],
            [new AiOutstandingWarningServiceModel(AiWarningKind.SafetyCaution, "Check the set.", proposalId)]);

        _ingredients.Gaps =
            [new IngredientAllergenReviewServiceModel(flour, "flour", IngredientAllergenReviewState.AwaitingReview)];

        var result = await _facade.EvaluateAsync(RecipeId, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);

        var external = _business.LastExternal!;
        Assert.Equal(2, external.Ai.PendingChangeCount);
        Assert.Equal([proposalId], external.Ai.ProposalIds);
        Assert.Equal(AiWarningKind.SafetyCaution, Assert.Single(external.Ai.Warnings).Kind);
        Assert.Equal(flour, Assert.Single(external.AllergenReviewGaps).IngredientId);
    }

    private static RecipeReadinessFacts Facts(IReadOnlyList<RecipeReadinessIngredientLine> lines) => new()
    {
        RecipeId = RecipeId,
        RecipeRowVersion = [1, 2, 3, 4, 5, 6, 7, 8],
        EvaluatedVersionId = Guid.NewGuid(),
        EvaluatedVersionNumber = 1,
        Title = "Olive oil cake",
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
        IngredientLines = lines,
        OpenIssues = [],
    };

    /// <summary>
    /// Records what the facade handed down, and runs the real evaluator so a successful path produces a real result.
    /// </summary>
    private sealed class StubReadinessBusiness : IRecipeReadinessBusiness
    {
        /// <summary>Null is a recipe the caller may not see.</summary>
        public RecipeReadinessFacts? Facts { get; set; }

        public RecipeReadinessExternalFacts? LastExternal { get; private set; }

        public int Evaluations { get; private set; }

        public Task<OperationResult<RecipeReadinessFacts>> FindFactsAsync(
            Guid recipeId, CancellationToken cancellationToken) =>
            Task.FromResult(Facts is null
                ? OperationResult<RecipeReadinessFacts>.Failure(new OperationError(
                    RecipeErrorCodes.RecipeNotFound,
                    "That recipe could not be found.",
                    new Dictionary<string, string[]>()))
                : OperationResult<RecipeReadinessFacts>.Success(Facts));

        public RecipeReadinessServiceModel Evaluate(
            RecipeReadinessFacts facts, RecipeReadinessExternalFacts external)
        {
            LastExternal = external;
            Evaluations++;

            return RecipeReadinessEvaluator.Evaluate(facts, external, new RecipeReadinessOptions());
        }
    }

    private sealed class StubProposalFacade : IAiProposalFacade
    {
        public AiOutstandingSummaryServiceModel Summary { get; set; } = new(0, [], []);

        public int Calls { get; private set; }

        public Task<AiOutstandingSummaryServiceModel> SummarizeOutstandingAsync(
            Guid recipeId, CancellationToken cancellationToken)
        {
            Calls++;

            return Task.FromResult(Summary);
        }

        // Not exercised: the readiness facade asks one question of this module.
        public Task<IdempotentOutcome<AiProposalStatusServiceModel>> RequestAsync(
            Guid recipeId,
            RequestAiProposalViewModel model,
            string? idempotencyKey,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<OperationResult<AiProposalStatusServiceModel>> GetAsync(
            Guid recipeId, Guid requestId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<OperationResult<AiProposalDispositionServiceModel>> DispositionAsync(
            string actorUserId,
            Guid recipeId,
            Guid requestId,
            AiProposalDispositionViewModel model,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class StubIngredientFacade : IIngredientFacade
    {
        public IReadOnlyList<IngredientAllergenReviewServiceModel> Gaps { get; set; } = [];

        public int Calls { get; private set; }

        public List<Guid> AskedAbout { get; } = [];

        public Task<IReadOnlyList<IngredientAllergenReviewServiceModel>> FindAllergenReviewGapsAsync(
            IReadOnlyCollection<Guid> ingredientIds, CancellationToken cancellationToken)
        {
            Calls++;
            AskedAbout.AddRange(ingredientIds);

            return Task.FromResult(Gaps);
        }

        // Not exercised: the readiness facade asks one question of this module.
        public Task<OperationResult<CursorPageServiceModel<IngredientServiceModel>>> ListIngredientsAsync(
            IngredientQueryViewModel model, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<OperationResult<IReadOnlyList<IngredientMatchResult>>> ResolveCandidatesAsync(
            IReadOnlyList<string> candidateTexts, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> IsUsableAsync(Guid ingredientId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}

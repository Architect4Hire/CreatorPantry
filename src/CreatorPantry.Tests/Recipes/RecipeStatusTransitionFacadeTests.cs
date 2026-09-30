using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Recipes.Business;
using CreatorPantry.Domain.Modules.Recipes.Facade;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Recipes;

/// <summary>
/// What the transition facade composes: when it asks for a readiness evaluation, when it does not, and what
/// it does with the answer (TESTRUN-005).
/// </summary>
/// <remarks>
/// The facade exists because the approval's gate crosses module boundaries Business may not. Under test here
/// is that arrangement rather than any rule — <see cref="RecipeTransitionBusinessTests"/> covers what
/// Business then does with what it was handed.
/// </remarks>
public sealed class RecipeStatusTransitionFacadeTests
{
    private static readonly Guid RecipeId = Guid.NewGuid();

    private readonly StubTransitionBusiness _business = new();
    private readonly StubReadinessFacade _readiness = new();
    private readonly IRecipeStatusTransitionFacade _facade;

    public RecipeStatusTransitionFacadeTests() =>
        _facade = new ServiceCollection()
            .AddSingleton<IRecipeBusiness>(_business)
            .AddSingleton<IRecipeReadinessFacade>(_readiness)
            .AddSingleton<IRecipeStatusTransitionFacade, RecipeStatusTransitionFacade>()
            .BuildServiceProvider()
            .GetRequiredService<IRecipeStatusTransitionFacade>();

    private Task<OperationResult<RecipeDetailServiceModel>> TransitionAsync(RecipeStatus target) =>
        _facade.TransitionAsync(
            RecipeId, target, "Because.", "user-1", "AQIDBAUGBwg=", TestContext.Current.CancellationToken);

    /// <summary>
    /// The approval is the one move that needs an evaluation, and the facade makes it rather than accepting
    /// one. A caller cannot hand in a verdict.
    /// </summary>
    [Fact]
    public async Task An_approval_is_evaluated_before_business_is_called()
    {
        await TransitionAsync(RecipeStatus.Approved);

        Assert.Equal(1, _readiness.Calls);
        Assert.Equal(RecipeId, _readiness.RequestedRecipeId);
        Assert.NotNull(_business.Readiness);
    }

    /// <summary>Every other move is passed straight down with no evaluation at all.</summary>
    [Theory]
    [InlineData(RecipeStatus.InDevelopment)]
    [InlineData(RecipeStatus.Testing)]
    [InlineData(RecipeStatus.ReadyForReview)]
    [InlineData(RecipeStatus.Archived)]
    [InlineData(RecipeStatus.Draft)]
    public async Task Another_move_is_not_evaluated(RecipeStatus target)
    {
        await TransitionAsync(target);

        Assert.Equal(0, _readiness.Calls);
        Assert.Null(_business.Readiness);
        Assert.Equal(target, _business.Target);
    }

    /// <summary>
    /// A refused evaluation is the answer, which for an invisible recipe is the 404 this route must give
    /// anyway — and Business is never reached, so nothing is loaded on behalf of somebody who cannot see it.
    /// </summary>
    [Fact]
    public async Task A_refused_evaluation_is_returned_and_business_is_not_called()
    {
        _readiness.Error = new OperationError(
            RecipeErrorCodes.RecipeNotFound, "That recipe could not be found.", new Dictionary<string, string[]>());

        var result = await TransitionAsync(RecipeStatus.Approved);

        Assert.False(result.Succeeded);
        Assert.Equal(RecipeErrorCodes.RecipeNotFound, result.Error!.Code);
        Assert.Equal(0, _business.Calls);
    }

    /// <summary>The request's own values reach Business unchanged; the facade adds only the evaluation.</summary>
    [Fact]
    public async Task The_request_is_passed_down_intact()
    {
        await TransitionAsync(RecipeStatus.ReadyForReview);

        Assert.Equal(RecipeId, _business.RequestedRecipeId);
        Assert.Equal(RecipeStatus.ReadyForReview, _business.Target);
        Assert.Equal("Because.", _business.Reason);
        Assert.Equal("user-1", _business.ActorUserId);
        Assert.Equal("AQIDBAUGBwg=", _business.Token);
    }

    private sealed class StubReadinessFacade : IRecipeReadinessFacade
    {
        public int Calls { get; private set; }

        public Guid RequestedRecipeId { get; private set; }

        public OperationError? Error { get; set; }

        public Task<OperationResult<RecipeReadinessServiceModel>> EvaluateAsync(
            Guid recipeId, CancellationToken cancellationToken)
        {
            Calls++;
            RequestedRecipeId = recipeId;

            return Task.FromResult(Error is null
                ? OperationResult<RecipeReadinessServiceModel>.Success(new RecipeReadinessServiceModel(
                    recipeId, Guid.NewGuid(), 1, "AQIDBAUGBwg=", RecipeReadinessCatalogue.Version,
                    false, 0, 0, [], [], []))
                : OperationResult<RecipeReadinessServiceModel>.Failure(Error));
        }
    }

    private sealed class StubTransitionBusiness : StubRecipeBusinessBase
    {
        public int Calls { get; private set; }

        public Guid RequestedRecipeId { get; private set; }

        public RecipeStatus Target { get; private set; }

        public string? Reason { get; private set; }

        public RecipeReadinessServiceModel? Readiness { get; private set; }

        public string? ActorUserId { get; private set; }

        public string? Token { get; private set; }

        public override Task<OperationResult<RecipeDetailServiceModel>> TransitionAsync(
            Guid recipeId,
            RecipeStatus target,
            string? reason,
            RecipeReadinessServiceModel? readiness,
            string actorUserId,
            string? expectedConcurrencyToken,
            CancellationToken cancellationToken)
        {
            Calls++;
            (RequestedRecipeId, Target, Reason, Readiness) = (recipeId, target, reason, readiness);
            (ActorUserId, Token) = (actorUserId, expectedConcurrencyToken);

            return Task.FromResult(OperationResult<RecipeDetailServiceModel>.Failure(new OperationError(
                RecipeErrorCodes.RecipeNotFound, "Not the subject of this test.", new Dictionary<string, string[]>())));
        }
    }
}

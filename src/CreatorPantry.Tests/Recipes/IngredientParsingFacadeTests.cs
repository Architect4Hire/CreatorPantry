using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Ingredients.Facade;
using CreatorPantry.Domain.Modules.Ingredients.Managers;
using CreatorPantry.Domain.Modules.Measurement.Facade;
using CreatorPantry.Domain.Modules.Measurement.Managers;
using CreatorPantry.Domain.Modules.Recipes.Facade;
using CreatorPantry.Domain.Modules.Recipes.Managers;

namespace CreatorPantry.Tests.Recipes;

/// <summary>
/// <see cref="IngredientParsingFacade"/> below the HTTP layer, where cancellation can actually be observed —
/// an aborted HTTP connection has no server-visible status to assert on, so this checks the thing the codebase
/// can check: that a cancelled token reaches the downstream calls rather than being swallowed or ignored.
/// </summary>
public sealed class IngredientParsingFacadeTests
{
    [Fact]
    public async Task Cancellation_propagates_into_the_downstream_matchers()
    {
        var facade = new IngredientParsingFacade(
            new ParseIngredientLinesViewModelValidator(),
            new CancellationCheckingIngredientFacade(),
            new CancellationCheckingMeasurementFacade());

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            facade.ParseAsync(new ParseIngredientLinesViewModel(["2 tsp kosher salt"]), cts.Token));
    }

    [Fact]
    public async Task A_valid_request_never_reaches_the_downstream_matchers_uncancelled_and_succeeds()
    {
        var facade = new IngredientParsingFacade(
            new ParseIngredientLinesViewModelValidator(),
            new CancellationCheckingIngredientFacade(),
            new CancellationCheckingMeasurementFacade());

        var result = await facade.ParseAsync(
            new ParseIngredientLinesViewModel(["2 tsp kosher salt"]), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Single(result.Value!.Lines);
    }

    /// <summary>Throws exactly the way a real EF-backed read does when the token is already cancelled.</summary>
    private sealed class CancellationCheckingIngredientFacade : IIngredientFacade
    {
        public Task<OperationResult<CursorPageServiceModel<IngredientServiceModel>>> ListIngredientsAsync(
            IngredientQueryViewModel model, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<OperationResult<IReadOnlyList<IngredientMatchResult>>> ResolveCandidatesAsync(
            IReadOnlyList<string> candidateTexts, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult(OperationResult<IReadOnlyList<IngredientMatchResult>>.Success(
                candidateTexts.Select(text => new IngredientMatchResult { InputText = text }).ToList()));
        }

        public Task<bool> IsUsableAsync(Guid ingredientId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class CancellationCheckingMeasurementFacade : IMeasurementFacade
    {
        public Task<Domain.Managers.Reference.MeasurementDimension?> FindUsableUnitDimensionAsync(
            Guid unitId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<OperationResult<CursorPageServiceModel<MeasurementUnitServiceModel>>> ListUnitsAsync(
            MeasurementUnitQueryViewModel model, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<MeasurementUnitServiceModel>> FindUnitsByIdsAsync(
            IReadOnlyCollection<Guid> unitIds, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<OperationResult<IReadOnlyList<UnitMatchResult>>> ResolveCandidatesAsync(
            IReadOnlyList<string> candidateTexts, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult(OperationResult<IReadOnlyList<UnitMatchResult>>.Success(
                candidateTexts.Select(text => new UnitMatchResult { InputText = text }).ToList()));
        }
    }
}

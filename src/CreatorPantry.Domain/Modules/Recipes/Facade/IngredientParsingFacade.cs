using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Ingredients.Facade;
using CreatorPantry.Domain.Modules.Ingredients.Managers;
using CreatorPantry.Domain.Modules.Measurement.Facade;
using CreatorPantry.Domain.Modules.Measurement.Managers;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Recipes.Facade;

internal sealed class IngredientParsingFacade(
    IValidator<ParseIngredientLinesViewModel> validator,
    IIngredientFacade ingredients,
    IMeasurementFacade units) : IIngredientParsingFacade
{
    public async Task<OperationResult<ParseIngredientLinesResult>> ParseAsync(
        ParseIngredientLinesViewModel model, CancellationToken cancellationToken)
    {
        var validation = await validator.ValidateAsync(model, cancellationToken);
        if (!validation.IsValid)
        {
            return OperationResult<ParseIngredientLinesResult>.Failure(OperationError.Validation(
                IngredientParsingErrorCodes.LinesInvalidRequest,
                "The lines could not be accepted as submitted.",
                validation.Errors.Select(failure => (failure.PropertyName, failure.ErrorMessage))));
        }

        var tokens = model.Lines!.Select(line => IngredientLineTokenizer.Tokenize(line!)).ToList();

        // Positions, not values: two lines can share the same ingredient text ("2 cups flour" twice), and
        // ResolveCandidatesAsync preserves order rather than deduplicating — so results are zipped back onto
        // the line they came from by index, never matched back up by comparing text.
        var ingredientCandidates = IndexedCandidates(tokens, line => line.IngredientText?.Text);
        var unitCandidates = IndexedCandidates(tokens, line => line.UnitCandidate?.Text);

        var ingredientResults = await ResolveAsync(
            ingredientCandidates, ingredients.ResolveCandidatesAsync, cancellationToken);
        if (!ingredientResults.Succeeded)
        {
            return OperationResult<ParseIngredientLinesResult>.Failure(ingredientResults.Error!);
        }

        var unitResults = await ResolveAsync(unitCandidates, units.ResolveCandidatesAsync, cancellationToken);
        if (!unitResults.Succeeded)
        {
            return OperationResult<ParseIngredientLinesResult>.Failure(unitResults.Error!);
        }

        var ingredientByLine = ByLine(ingredientCandidates, ingredientResults.Value!);
        var unitByLine = ByLine(unitCandidates, unitResults.Value!);

        var parsedLines = tokens
            .Select((line, index) => new ParsedIngredientLine(
                line,
                ingredientByLine.GetValueOrDefault(index),
                unitByLine.GetValueOrDefault(index)))
            .ToList();

        return OperationResult<ParseIngredientLinesResult>.Success(new ParseIngredientLinesResult(parsedLines));
    }

    private static List<(int LineIndex, string Text)> IndexedCandidates(
        IReadOnlyList<IngredientLineTokens> tokens, Func<IngredientLineTokens, string?> selectCandidate) =>
        tokens
            .Select((line, index) => (LineIndex: index, Text: selectCandidate(line)))
            .Where(candidate => candidate.Text is not null)
            .Select(candidate => (candidate.LineIndex, Text: candidate.Text!))
            .ToList();

    private static async Task<OperationResult<IReadOnlyList<TResult>>> ResolveAsync<TResult>(
        IReadOnlyList<(int LineIndex, string Text)> candidates,
        Func<IReadOnlyList<string>, CancellationToken, Task<OperationResult<IReadOnlyList<TResult>>>> resolve,
        CancellationToken cancellationToken) =>
        candidates.Count == 0
            ? OperationResult<IReadOnlyList<TResult>>.Success([])
            : await resolve(candidates.Select(candidate => candidate.Text).ToList(), cancellationToken);

    private static Dictionary<int, TResult> ByLine<TResult>(
        IReadOnlyList<(int LineIndex, string Text)> candidates, IReadOnlyList<TResult> results) =>
        candidates
            .Select((candidate, position) => (candidate.LineIndex, Result: results[position]))
            .ToDictionary(pair => pair.LineIndex, pair => pair.Result);
}

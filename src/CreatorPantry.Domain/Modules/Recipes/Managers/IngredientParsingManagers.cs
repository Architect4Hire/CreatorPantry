using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Modules.Ingredients.Managers;
using CreatorPantry.Domain.Modules.Measurement.Managers;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>Limits for a batch ingredient-line parse request (ING-001, 7.4).</summary>
public static class IngredientParsingPolicy
{
    /// <summary>Refused above this many lines in one request — a full recipe's ingredient list, not a cookbook.</summary>
    public const int MaxLines = 100;

    /// <summary>
    /// Refused above this many characters for a single line. Kept equal to
    /// <see cref="ReferencePolicy.MaxMatchCandidateLength"/>: a line's ingredient-text and unit-candidate
    /// spans are substrings of the line, so bounding the line this way guarantees neither span can ever
    /// separately trip the reference matcher's own length limit.
    /// </summary>
    public const int MaxLineLength = ReferencePolicy.MaxMatchCandidateLength;
}

/// <summary>Stable error codes for the ingredient-tools routes. Renaming one is a breaking API change.</summary>
public static class IngredientParsingErrorCodes
{
    /// <summary>The request body failed shape or format validation — too many lines, an over-long line, or a blank one.</summary>
    public const string LinesInvalidRequest = "recipes.ingredient-lines.invalid_request";
}

/// <summary>A batch of free-form recipe ingredient lines to tokenize and match. Nothing here is persisted.</summary>
public sealed record ParseIngredientLinesViewModel(IReadOnlyList<string?>? Lines);

public sealed class ParseIngredientLinesViewModelValidator : AbstractValidator<ParseIngredientLinesViewModel>
{
    public ParseIngredientLinesViewModelValidator()
    {
        RuleFor(model => model.Lines!)
            .Cascade(CascadeMode.Stop)
            .NotNull()
                .WithMessage("At least one line is required.")
            .Must(lines => lines.Count > 0)
                .WithMessage("At least one line is required.")
            .Must(lines => lines.Count <= IngredientParsingPolicy.MaxLines)
                .WithMessage($"No more than {IngredientParsingPolicy.MaxLines} lines may be parsed at once.")
            .Must(lines => lines.All(line => !string.IsNullOrWhiteSpace(line)))
                .WithMessage("A line cannot be blank.")
            .Must(lines => lines.All(line => line!.Length <= IngredientParsingPolicy.MaxLineLength))
                .WithMessage($"A line can be at most {IngredientParsingPolicy.MaxLineLength} characters.")
            .OverridePropertyName(nameof(ParseIngredientLinesViewModel.Lines));
    }
}

/// <summary>
/// One submitted line, tokenized and matched. A proposal only — nothing here is written to a recipe, and a
/// creator's line is never rewritten by it (recipes.md).
/// </summary>
/// <param name="Tokens">7.2's segmentation — quantity/range, unit candidate, ingredient text, preparation text.</param>
/// <param name="IngredientMatch">
/// 7.3's ingredient-catalogue resolution for <see cref="Tokens"/>' ingredient text, or <see langword="null"/>
/// when the line is a group marker or the tokenizer found no ingredient text to match.
/// </param>
/// <param name="UnitMatch">
/// 7.3's unit-catalogue resolution for <see cref="Tokens"/>' unit candidate, or <see langword="null"/> when
/// there was no unit candidate to match.
/// </param>
public sealed record ParsedIngredientLine(
    IngredientLineTokens Tokens,
    IngredientMatchResult? IngredientMatch,
    UnitMatchResult? UnitMatch);

/// <summary>The parsed batch, in the order the lines were submitted.</summary>
public sealed record ParseIngredientLinesResult(IReadOnlyList<ParsedIngredientLine> Lines);

using CreatorPantry.Domain.Managers.Paging;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// Shape validation for <see cref="TestRunHistoryViewModel"/>: the two things a validator can settle here.
/// </summary>
/// <remarks>
/// <para>
/// Edge validation only, and deliberately thin for the reason <see cref="RecipeSearchViewModelValidator"/> gives:
/// almost every parameter on this query is either a value this module must parse into a typed filter — which
/// <see cref="TestRunHistoryQueryFactory"/> does, because a validator that only said "that is not an outcome"
/// would leave the factory parsing it again — or a value the binder has already vouched for.
/// </para>
/// <para>
/// The <c>cursor</c> rule keeps <see cref="ReferenceCursor.TryDecode"/>'s structural check at the edge, so a
/// cursor that is not even base64 is refused before any workspace or recipe is resolved. Whether a well-formed
/// cursor belongs to <em>this</em> recipe's history under <em>these</em> filters is a different question and not
/// one a validator can ask: it has no idea which workspace was resolved or which recipe the route named.
/// </para>
/// <para>
/// <strong><c>limit</c> is not validated</strong>, matching every other paged route. An out-of-range page size is
/// clamped rather than refused, so a client cannot fail a read by asking for too much; validating it would turn a
/// clamp into a rejection.
/// </para>
/// </remarks>
public sealed class TestRunHistoryViewModelValidator : AbstractValidator<TestRunHistoryViewModel>
{
    public TestRunHistoryViewModelValidator()
    {
        RuleFor(model => model.Cursor)
            .Must(value => ReferenceCursor.TryDecode(value, out _))
            .WithMessage("Pass the previous page's nextCursor exactly as it was returned.")
            .When(model => !string.IsNullOrEmpty(model.Cursor))
            .OverridePropertyName("cursor");

        // An inverted range matches nothing, which is legal and almost never intended — and "no tests" is an
        // answer a creator cannot debug. Refusing it names the mistake instead, exactly as the library search
        // does with its two ranges.
        RuleFor(model => model.TestedBefore)
            .Must((model, before) => before > model.TestedFrom)
            .WithMessage("testedBefore must be later than testedFrom.")
            .When(model => model.TestedFrom is not null && model.TestedBefore is not null)
            .OverridePropertyName("testedBefore");
    }
}

using CreatorPantry.Domain.Managers.Patching;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// Shape and format validation for <see cref="UpdateRecipeTestRunViewModel"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Every content rule is gated on <see cref="PatchField{T}.IsSubmitted"/>,</strong> for the reason
/// <see cref="UpdateRecipeViewModelValidator"/> gives: a field the body never mentioned is not being changed,
/// so validating it would refuse a good edit over something the caller did not touch — and on a test recorded
/// before a limit tightened, it would leave the creator unable to correct their own notes at all.
/// </para>
/// <para>
/// <strong>What is deliberately not here.</strong> Whether the submitted unit exists needs a lookup, so it
/// stays in the facade. Whether a yield unit still has a quantity beside it cannot be answered from this body
/// alone — a patch names only part of the run — so it belongs to Business, which can see the merged result.
/// Nor is <c>testedAt</c> checked against the clock here, for the same reason it was not on create.
/// </para>
/// </remarks>
public sealed class UpdateRecipeTestRunViewModelValidator : AbstractValidator<UpdateRecipeTestRunViewModel>
{
    public UpdateRecipeTestRunViewModelValidator()
    {
        // Required, and worth stating before anything else: an edit with nothing to check against cannot be
        // applied safely whatever else it says.
        RuleFor(model => model.ExpectedConcurrencyToken)
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage("Send the test's concurrency token with your edit.")
            // Refused here rather than compared later: a token that could never have been issued is a client
            // bug, and reporting it as a conflict would send a creator looking for a collaborator who is not
            // there.
            .Must(RecipeConcurrencyToken.IsWellFormed)
                .WithMessage("That is not a concurrency token this API issued.")
            .OverridePropertyName(nameof(UpdateRecipeTestRunViewModel.ExpectedConcurrencyToken));

        // May be changed, never cleared: a test with no date is not a test.
        RuleFor(model => model.TestedAt)
            .Must(field => field.Value is not null).WithMessage("A test must keep the date it was run.")
            .When(model => model.TestedAt.IsSubmitted)
            .OverridePropertyName(nameof(UpdateRecipeTestRunViewModel.TestedAt));

        // Likewise: retracting a verdict is sending NotStated, which says something, rather than clearing the
        // field, which would say nothing about whether the tester had decided.
        RuleFor(model => model.Outcome)
            .Cascade(CascadeMode.Stop)
            .Must(field => field.Value is not null)
                .WithMessage("A test keeps an outcome; send NotStated to withdraw a verdict.")
            .Must(field => Enum.IsDefined(field.Value!.Value)).WithMessage("That is not a test outcome.")
            .When(model => model.Outcome.IsSubmitted)
            .OverridePropertyName(nameof(UpdateRecipeTestRunViewModel.Outcome));

        RuleFor(model => model.Rating)
            .Must(field => field.Value is null
                || field.Value is >= TestRunPolicy.MinRating and <= TestRunPolicy.MaxRating)
                .WithMessage($"A rating is between {TestRunPolicy.MinRating} and {TestRunPolicy.MaxRating}.")
            .When(model => model.Rating.IsSubmitted)
            .OverridePropertyName(nameof(UpdateRecipeTestRunViewModel.Rating));

        Text(
            model => model.EnvironmentNotes,
            TestRunPolicy.EnvironmentNotesMaxLength,
            nameof(UpdateRecipeTestRunViewModel.EnvironmentNotes));
        Text(
            model => model.EquipmentNotes,
            TestRunPolicy.EquipmentNotesMaxLength,
            nameof(UpdateRecipeTestRunViewModel.EquipmentNotes));
        Text(
            model => model.SummaryNotes,
            TestRunPolicy.SummaryNotesMaxLength,
            nameof(UpdateRecipeTestRunViewModel.SummaryNotes));
        Text(
            model => model.ActualYieldText,
            RecipePolicy.YieldTextMaxLength,
            nameof(UpdateRecipeTestRunViewModel.ActualYieldText));

        RuleFor(model => model.ActualYieldQuantity)
            .Must(field => field.Value is null or > 0m)
                .WithMessage("An actual yield must be greater than zero.")
            .When(model => model.ActualYieldQuantity.IsSubmitted)
            .OverridePropertyName(nameof(UpdateRecipeTestRunViewModel.ActualYieldQuantity));

        RuleFor(model => model.ActualYieldUnitId)
            .Must(field => field.Value != Guid.Empty).WithMessage("That is not a valid unit reference.")
            .When(model => model.ActualYieldUnitId.IsSubmitted)
            .OverridePropertyName(nameof(UpdateRecipeTestRunViewModel.ActualYieldUnitId));

        Minutes(model => model.ActualPrepTimeMinutes, nameof(UpdateRecipeTestRunViewModel.ActualPrepTimeMinutes));
        Minutes(model => model.ActualCookTimeMinutes, nameof(UpdateRecipeTestRunViewModel.ActualCookTimeMinutes));
        Minutes(model => model.ActualRestTimeMinutes, nameof(UpdateRecipeTestRunViewModel.ActualRestTimeMinutes));
        Minutes(model => model.ActualTotalTimeMinutes, nameof(UpdateRecipeTestRunViewModel.ActualTotalTimeMinutes));

        RuleFor(model => model.Observations)
            .Must(field => Rows(field).Count <= TestRunPolicy.MaxObservationsPerRun)
                .WithMessage($"A test can carry at most {TestRunPolicy.MaxObservationsPerRun} observations.")
            .Must(field => Rows(field).All(observation => observation is not null))
                .WithMessage("An observation cannot be empty.")
            .Must(field => HaveNoRepeatedIds(Rows(field).Select(observation => observation?.Id)))
                .WithMessage("Two observations cannot name the same note.")
            .When(model => model.Observations.IsSubmitted)
            .OverridePropertyName(nameof(UpdateRecipeTestRunViewModel.Observations));

        RuleFor(model => model.Issues)
            .Must(field => Rows(field).Count <= TestRunPolicy.MaxIssuesPerRun)
                .WithMessage($"A test can carry at most {TestRunPolicy.MaxIssuesPerRun} issues.")
            .Must(field => Rows(field).All(issue => issue is not null))
                .WithMessage("An issue cannot be empty.")
            .Must(field => HaveNoRepeatedIds(Rows(field).Select(issue => issue?.Id)))
                .WithMessage("Two issues cannot name the same issue.")
            .When(model => model.Issues.IsSubmitted)
            .OverridePropertyName(nameof(UpdateRecipeTestRunViewModel.Issues));

        // Everything a single observation or issue can be wrong about, at its own position. Only for the lists
        // the body actually submitted — a list left alone has nothing to report.
        RuleFor(model => model).Custom((model, context) =>
        {
            if (model.Observations.IsSubmitted)
            {
                TestRunInputPositions.AddObservationFailures(
                    Rows(model.Observations),
                    RecipeInputMode.Update,
                    (path, message) => context.AddFailure(path, message));
            }

            if (model.Issues.IsSubmitted)
            {
                // Bounded against the observations this request submits, when it submits them — and against
                // nothing when it does not, because an index into a list the caller did not send cannot be
                // resolved. Business refuses that case, where the run's own notes are in hand.
                TestRunInputPositions.AddIssueFailures(
                    Rows(model.Issues),
                    model.Observations.IsSubmitted ? Rows(model.Observations).Count : int.MaxValue,
                    RecipeInputMode.Update,
                    (path, message) => context.AddFailure(path, message));
            }
        });
    }

    private void Text(
        Func<UpdateRecipeTestRunViewModel, PatchField<string?>> field,
        int maxLength,
        string name) =>
        RuleFor(model => field(model))
            .Must(submitted => (submitted.Value ?? string.Empty).Trim().Length <= maxLength)
                .WithMessage($"That can be at most {maxLength} characters.")
            .When(model => field(model).IsSubmitted)
            .OverridePropertyName(name);

    private void Minutes(Func<UpdateRecipeTestRunViewModel, PatchField<int?>> field, string name) =>
        RuleFor(model => field(model))
            .Must(submitted => submitted.Value is null or (>= 0 and <= RecipePolicy.MaxTimeMinutes))
                .WithMessage("Enter a time in minutes between 0 and one year.")
            .When(model => field(model).IsSubmitted)
            .OverridePropertyName(name);

    private static IReadOnlyList<T?> Rows<T>(PatchField<IReadOnlyList<T?>?> field) => field.Value ?? [];

    /// <summary>
    /// Whether the ids a submitted list carries are distinct.
    /// </summary>
    /// <remarks>
    /// Two entries naming one row would have the reconciliation apply both to it and then treat the loser as
    /// removed — a request whose result depends on which of two conflicting edits was written second. Refusing
    /// it is the only answer that is the same every time. Entries with no id are new rows and never collide.
    /// </remarks>
    private static bool HaveNoRepeatedIds(IEnumerable<Guid?> ids)
    {
        var named = ids.Where(id => id is not null).ToList();

        return named.Distinct().Count() == named.Count;
    }
}

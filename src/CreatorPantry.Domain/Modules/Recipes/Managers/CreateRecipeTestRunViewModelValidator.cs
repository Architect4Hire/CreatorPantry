using FluentValidation;

namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// Shape and format validation for <see cref="CreateRecipeTestRunViewModel"/>.
/// </summary>
/// <remarks>
/// <para>
/// What is deliberately absent is any check that the version <em>exists</em>, that the recipe is visible, or
/// that <c>testedAt</c> is in the past. The first two need a lookup and belong below, where a refusal can say
/// which recipe and which version it looked for; the third needs the clock, which is Business's to read.
/// </para>
/// <para>
/// Nothing here trusts the binder: every string is treated as nullable and trimmed before it is measured, and
/// the two lists are treated as possibly null and possibly holding nulls.
/// </para>
/// </remarks>
public sealed class CreateRecipeTestRunViewModelValidator : AbstractValidator<CreateRecipeTestRunViewModel>
{
    public CreateRecipeTestRunViewModelValidator()
    {
        // Required, unlike the duplicate route's: a test against "whichever version is current" is evidence
        // about nothing in particular. The floor matches CK_RecipeVersions_VersionNumber_Positive, so a number
        // no version could carry is a field error rather than a lookup that finds nothing.
        RuleFor(model => model.SourceVersionNumber)
            .Cascade(CascadeMode.Stop)
            .NotNull().WithMessage("Say which version was tested.")
            .GreaterThanOrEqualTo(RecipePolicy.FirstVersionNumber)
                .WithMessage($"A version number starts at {RecipePolicy.FirstVersionNumber}.");

        RuleFor(model => model.TestedAt)
            .NotNull().WithMessage("Say when this was tested.");

        RuleFor(model => model.Outcome)
            .Must(outcome => Enum.IsDefined(outcome!.Value)).WithMessage("That is not a test outcome.")
            .When(model => model.Outcome is not null);

        RuleFor(model => model.Rating)
            .InclusiveBetween(TestRunPolicy.MinRating, TestRunPolicy.MaxRating)
                .WithMessage($"A rating is between {TestRunPolicy.MinRating} and {TestRunPolicy.MaxRating}.")
            .When(model => model.Rating is not null);

        MaxLength(model => model.EnvironmentNotes, TestRunPolicy.EnvironmentNotesMaxLength, "The conditions note");
        MaxLength(model => model.EquipmentNotes, TestRunPolicy.EquipmentNotesMaxLength, "The equipment note");
        MaxLength(model => model.SummaryNotes, TestRunPolicy.SummaryNotesMaxLength, "A summary");
        MaxLength(model => model.ActualYieldText, RecipePolicy.YieldTextMaxLength, "A yield");

        RuleFor(model => model.ActualYieldQuantity)
            .GreaterThan(0m).WithMessage("An actual yield must be greater than zero.")
            .When(model => model.ActualYieldQuantity is not null);

        RuleFor(model => model.ActualYieldUnitId)
            .NotEqual(Guid.Empty).WithMessage("That is not a valid unit reference.")
            .When(model => model.ActualYieldUnitId is not null);

        // Mirrors CK_RecipeTestRuns_ActualYieldUnit_RequiresQuantity. The reverse is allowed: "got 10, not 12"
        // is a complete result with no unit in it.
        RuleFor(model => model.ActualYieldUnitId)
            .Must((model, _) => model.ActualYieldQuantity is not null)
                .WithMessage("A unit needs an amount to measure.")
            .When(model => model.ActualYieldUnitId is not null);

        // The same bounds CK_RecipeTestRuns_Times_Range puts in the database, so the common paste error — a
        // millisecond value into a field that means minutes — is a field error naming the field rather than a
        // constraint violation naming four of them at once.
        RuleFor(model => model.ActualPrepTimeMinutes).InclusiveBetween(0, RecipePolicy.MaxTimeMinutes)
            .WithMessage(TimeMessage).When(model => model.ActualPrepTimeMinutes is not null);

        RuleFor(model => model.ActualCookTimeMinutes).InclusiveBetween(0, RecipePolicy.MaxTimeMinutes)
            .WithMessage(TimeMessage).When(model => model.ActualCookTimeMinutes is not null);

        RuleFor(model => model.ActualRestTimeMinutes).InclusiveBetween(0, RecipePolicy.MaxTimeMinutes)
            .WithMessage(TimeMessage).When(model => model.ActualRestTimeMinutes is not null);

        RuleFor(model => model.ActualTotalTimeMinutes).InclusiveBetween(0, RecipePolicy.MaxTimeMinutes)
            .WithMessage(TimeMessage).When(model => model.ActualTotalTimeMinutes is not null);

        // Nothing checks the total against the sum of the parts, here or anywhere else. A real kitchen
        // overlaps prep with cooking, so a total below their sum is what the tester measured.

        RuleFor(model => model.Observations!)
            .Must(observations => observations.Count <= TestRunPolicy.MaxObservationsPerRun)
                .WithMessage($"A test can carry at most {TestRunPolicy.MaxObservationsPerRun} observations.")
            .Must(observations => observations.All(observation => observation is not null))
                .WithMessage("An observation cannot be empty.")
            .When(model => model.Observations is not null);

        RuleFor(model => model.Issues!)
            .Must(issues => issues.Count <= TestRunPolicy.MaxIssuesPerRun)
                .WithMessage($"A test can carry at most {TestRunPolicy.MaxIssuesPerRun} issues.")
            .Must(issues => issues.All(issue => issue is not null))
                .WithMessage("An issue cannot be empty.")
            .When(model => model.Issues is not null);

        // Everything a single observation or issue can be wrong about, reported at its own position.
        RuleFor(model => model).Custom((model, context) =>
        {
            TestRunInputPositions.AddObservationFailures(
                model.Observations, RecipeInputMode.Create, (path, message) => context.AddFailure(path, message));

            TestRunInputPositions.AddIssueFailures(
                model.Issues,
                model.Observations?.Count ?? 0,
                RecipeInputMode.Create,
                (path, message) => context.AddFailure(path, message));
        });
    }

    private const string TimeMessage = "A time must be in minutes between 0 and one year.";

    /// <summary>
    /// Bounds one optional free-text field, measured after trimming.
    /// </summary>
    /// <remarks>
    /// Trimmed before it is measured for the reason <see cref="DuplicateRecipeViewModelValidator"/> trims a
    /// title: surrounding whitespace is not something a creator wrote, and refusing a note for being four
    /// characters over when three of them are spaces would be a limit nobody could see.
    /// </remarks>
    private void MaxLength(
        System.Linq.Expressions.Expression<Func<CreateRecipeTestRunViewModel, string?>> field,
        int maximum,
        string noun) =>
        RuleFor(field)
            .Must(value => (value ?? string.Empty).Trim().Length <= maximum)
                .WithMessage($"{noun} can be at most {maximum} characters.");
}

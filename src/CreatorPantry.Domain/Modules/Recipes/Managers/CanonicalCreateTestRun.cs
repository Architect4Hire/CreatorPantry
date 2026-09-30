namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// A create-test-run request reduced to what it actually means. Two requests with the same canonical form
/// record the same test.
/// </summary>
/// <remarks>
/// The <see cref="CanonicalCreateRecipe"/> and <see cref="CanonicalDuplicateRecipe"/> counterpart, and it
/// exists for the same reason: the facade hashes it as the idempotency fingerprint and Business reads the
/// request from it, so "same fingerprint" and "same test" cannot drift apart. It carries no workspace, no
/// actor and no server timestamp — those would make every request unique, which is the opposite of what a
/// fingerprint is for. <see cref="TestedAt"/> is the caller's own value and belongs here: two tests of the
/// same version on different days are two tests.
/// </remarks>
public sealed record CanonicalCreateTestRun
{
    /// <summary>The version that was cooked. Non-null by validation.</summary>
    public required int SourceVersionNumber { get; init; }

    /// <summary>When the cooking happened, as sent. Non-null by validation.</summary>
    public required DateTimeOffset TestedAt { get; init; }

    public required TestRunOutcome Outcome { get; init; }

    public int? Rating { get; init; }

    public string? EnvironmentNotes { get; init; }

    public string? EquipmentNotes { get; init; }

    public string? SummaryNotes { get; init; }

    public string? ActualYieldText { get; init; }

    public decimal? ActualYieldQuantity { get; init; }

    public Guid? ActualYieldUnitId { get; init; }

    public int? ActualPrepTimeMinutes { get; init; }

    public int? ActualCookTimeMinutes { get; init; }

    public int? ActualRestTimeMinutes { get; init; }

    public int? ActualTotalTimeMinutes { get; init; }

    /// <summary>The notes, in submitted order, with the nulls a malformed list may carry removed.</summary>
    public required IReadOnlyList<CanonicalTestObservation> Observations { get; init; }

    /// <summary>The issues, in submitted order.</summary>
    public required IReadOnlyList<CanonicalTestIssue> Issues { get; init; }

    /// <summary>Reduces a validated request to its meaning.</summary>
    /// <remarks>
    /// Every string is trimmed and an empty one becomes null, so a note sent as <c>""</c> and one left out are
    /// the same request rather than two that would each get their own idempotency record. Internal spacing is
    /// the creator's and is not touched — recipes.md makes their text canonical.
    /// </remarks>
    public static CanonicalCreateTestRun From(CreateRecipeTestRunViewModel model) =>
        new()
        {
            SourceVersionNumber = model.SourceVersionNumber!.Value,
            TestedAt = model.TestedAt!.Value,
            Outcome = model.Outcome ?? TestRunOutcome.NotStated,
            Rating = model.Rating,
            EnvironmentNotes = Clean(model.EnvironmentNotes),
            EquipmentNotes = Clean(model.EquipmentNotes),
            SummaryNotes = Clean(model.SummaryNotes),
            ActualYieldText = Clean(model.ActualYieldText),
            ActualYieldQuantity = model.ActualYieldQuantity,
            ActualYieldUnitId = model.ActualYieldUnitId,
            ActualPrepTimeMinutes = model.ActualPrepTimeMinutes,
            ActualCookTimeMinutes = model.ActualCookTimeMinutes,
            ActualRestTimeMinutes = model.ActualRestTimeMinutes,
            ActualTotalTimeMinutes = model.ActualTotalTimeMinutes,

            // Indexed before the nulls are dropped, because an issue's ObservationIndex points into the list
            // as submitted. Validation has already refused a list containing nulls, so this is defensive
            // rather than load-bearing — but a canonicalizer that silently renumbered would be a very quiet
            // way to file an issue against the wrong note.
            Observations =
            [
                .. (model.Observations ?? [])
                    .Select((observation, index) => (observation, index))
                    .Where(entry => entry.observation is not null)
                    .Select(entry => new CanonicalTestObservation(
                        entry.index,
                        entry.observation!.Kind ?? TestObservationKind.Unspecified,
                        entry.observation.Text!.Trim())),
            ],
            Issues =
            [
                .. (model.Issues ?? [])
                    .Where(issue => issue is not null)
                    .Select(issue => new CanonicalTestIssue(
                        issue!.Severity!.Value,
                        issue.Title!.Trim(),
                        Clean(issue.Description),
                        issue.ObservationIndex)),
            ],
        };

    /// <summary>
    /// What makes two create-test-run requests the same one.
    /// </summary>
    /// <param name="recipeId">The recipe, from the route.</param>
    /// <remarks>
    /// <para>
    /// The recipe is part of it, or one key would cover recording a test against every recipe the caller owns.
    /// Everything the caller sent is part of it, because two tests that differ in any recorded fact are two
    /// tests the creator meant to have — a replay that returned the first when the second was asked for would
    /// lose one of them, and losing a test is losing evidence.
    /// </para>
    /// <para>
    /// There is no concurrency token to include, as on a duplicate and for the same reason. That makes this
    /// fingerprint stable across time rather than across one state of the recipe, which is right for a create:
    /// retrying the same submission hours later is still the same test and must not produce a second one.
    /// </para>
    /// </remarks>
    public object Fingerprint(Guid recipeId) => new
    {
        RecipeId = recipeId,
        SourceVersionNumber,
        TestedAt,
        Outcome,
        Rating,
        EnvironmentNotes,
        EquipmentNotes,
        SummaryNotes,
        ActualYieldText,
        ActualYieldQuantity,
        ActualYieldUnitId,
        ActualPrepTimeMinutes,
        ActualCookTimeMinutes,
        ActualRestTimeMinutes,
        ActualTotalTimeMinutes,
        Observations,
        Issues,
    };

    private static string? Clean(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}

/// <summary>One submitted note, with the position it arrived at so an issue can still find it.</summary>
public sealed record CanonicalTestObservation(int SubmittedIndex, TestObservationKind Kind, string Text);

/// <summary>One submitted issue, still pointing at a note by its submitted position.</summary>
public sealed record CanonicalTestIssue(
    TestIssueSeverity Severity, string Title, string? Description, int? ObservationIndex);

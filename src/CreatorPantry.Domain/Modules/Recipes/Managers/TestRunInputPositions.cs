namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// Where a refused observation or issue is, as a field path the client can resolve to the row it submitted.
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="RecipeInputPositions"/> counterpart, built on the same rule and for the same reason:
/// anything one submitted row can be wrong about on its own is reported at its position, because "A title is
/// required." about a test with a dozen issues is a sentence no creator can act on. Rules about a whole list —
/// how many there may be — stay on the list's own key, since a total is not about any one row.
/// </para>
/// <para>
/// The first segment is PascalCase and every later one camelCase deliberately:
/// <c>OperationError.Validation</c> lowercases only a key's first character, so this is the shape that reads
/// as <c>observations[0].text</c> on the wire.
/// </para>
/// <para>
/// Walked by index rather than through a filtering helper, because a null entry still occupies a position and
/// an index that skipped it would point every later error at the wrong row.
/// </para>
/// </remarks>
public static class TestRunInputPositions
{
    public static string ObservationPath(int index, string field) => $"Observations[{index}].{field}";

    public static string IssuePath(int index, string field) => $"Issues[{index}].{field}";

    /// <summary>Reports every observation problem that belongs to one submitted note, at its position.</summary>
    /// <remarks>
    /// A null entry is skipped rather than reported: that is a malformed request, the list-level rule refuses
    /// it under the list's own key, and there is nothing at that position for a creator to fix.
    /// </remarks>
    public static void AddObservationFailures(
        IReadOnlyList<TestObservationInputViewModel?>? observations,
        RecipeInputMode mode,
        Action<string, string> fail)
    {
        if (observations is null)
        {
            return;
        }

        for (var index = 0; index < observations.Count; index++)
        {
            var observation = observations[index];
            if (observation is null)
            {
                continue;
            }

            if (mode == RecipeInputMode.Create && observation.Id is not null)
            {
                fail(ObservationPath(index, "id"), "A new test's observations cannot name an existing note.");
            }

            if (mode == RecipeInputMode.Update && observation.Id == Guid.Empty)
            {
                fail(ObservationPath(index, "id"), "That is not a valid observation reference.");
            }

            if (string.IsNullOrWhiteSpace(observation.Text))
            {
                fail(ObservationPath(index, "text"), "An observation cannot be blank.");
            }
            else if (Trim(observation.Text).Length > TestRunPolicy.ObservationTextMaxLength)
            {
                fail(
                    ObservationPath(index, "text"),
                    $"An observation can be at most {TestRunPolicy.ObservationTextMaxLength} characters.");
            }

            // Unspecified is a legitimate kind, so only a value outside the enum is wrong. The binder maps an
            // unknown name to null rather than to a number, which the enum check below catches either way.
            if (observation.Kind is { } kind && !Enum.IsDefined(kind))
            {
                fail(ObservationPath(index, "kind"), "That is not an observation kind.");
            }
        }
    }

    /// <summary>Reports every issue problem that belongs to one submitted issue, at its position.</summary>
    /// <param name="observationCount">
    /// How many observations this same request submitted, which is what bounds an
    /// <see cref="TestIssueInputViewModel.ObservationIndex"/>. Passed in rather than read from the issue,
    /// because an issue cannot see the list it points into.
    /// </param>
    public static void AddIssueFailures(
        IReadOnlyList<TestIssueInputViewModel?>? issues,
        int observationCount,
        RecipeInputMode mode,
        Action<string, string> fail)
    {
        if (issues is null)
        {
            return;
        }

        for (var index = 0; index < issues.Count; index++)
        {
            var issue = issues[index];
            if (issue is null)
            {
                continue;
            }

            if (mode == RecipeInputMode.Create && issue.Id is not null)
            {
                fail(IssuePath(index, "id"), "A new test's issues cannot name an existing issue.");
            }

            if (mode == RecipeInputMode.Update && issue.Id == Guid.Empty)
            {
                fail(IssuePath(index, "id"), "That is not a valid issue reference.");
            }

            // Required, and with no default to fall back on: CK_TestIssues_Severity_Specified refuses zero at
            // the database, and the reason it does is that a missing severity landing on Minor would
            // under-report a blocking problem.
            if (issue.Severity is not { } severity)
            {
                fail(IssuePath(index, "severity"), "Say how badly this affects the recipe.");
            }
            else if (!Enum.IsDefined(severity) || severity == default)
            {
                fail(IssuePath(index, "severity"), "That is not an issue severity.");
            }

            if (string.IsNullOrWhiteSpace(issue.Title))
            {
                fail(IssuePath(index, "title"), "Give the issue a one-line summary.");
            }
            else if (Trim(issue.Title).Length > TestRunPolicy.IssueTitleMaxLength)
            {
                fail(
                    IssuePath(index, "title"),
                    $"An issue summary can be at most {TestRunPolicy.IssueTitleMaxLength} characters.");
            }

            if (Trim(issue.Description).Length > TestRunPolicy.IssueDescriptionMaxLength)
            {
                fail(
                    IssuePath(index, "description"),
                    $"An issue description can be at most {TestRunPolicy.IssueDescriptionMaxLength} characters.");
            }

            // Bounded against the observations this same request carries. Checked here rather than left to
            // the write, because an index past the end is a client mistake with an obvious remedy, and the
            // alternative is an issue silently filed against nothing.
            if (issue.ObservationIndex is { } observationIndex
                && (observationIndex < 0 || observationIndex >= observationCount))
            {
                fail(
                    IssuePath(index, "observationIndex"),
                    "That does not name one of this test's observations.");
            }
        }
    }

    private static string Trim(string? value) => value?.Trim() ?? string.Empty;
}

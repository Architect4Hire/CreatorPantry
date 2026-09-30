using CreatorPantry.Domain.Modules.Recipes.Data.Entities;

namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// One recorded test as a caller reads it back: what was cooked, what happened, and the token the next edit
/// must quote.
/// </summary>
/// <remarks>
/// <para>
/// Returned by the update so an editor can rebind from the response rather than re-reading — which it must be
/// able to do, because a second edit needs a token the first one moved.
/// </para>
/// <para>
/// Carries no storage URL and no asset id, because nothing on this route links media.
/// </para>
/// </remarks>
public sealed record RecipeTestRunServiceModel
{
    public required Guid Id { get; init; }

    public required Guid RecipeId { get; init; }

    /// <summary>The exact version that was cooked. Not changeable by an edit.</summary>
    public required Guid RecipeVersionId { get; init; }

    public required DateTimeOffset TestedAt { get; init; }

    /// <summary>The membership of whoever cooked it, not their Identity user id.</summary>
    public required Guid TestedByMembershipId { get; init; }

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

    /// <summary>What the whole thing took, as recorded. Never the sum of the three above.</summary>
    public int? ActualTotalTimeMinutes { get; init; }

    public required Guid CreatedByMembershipId { get; init; }

    public required Guid UpdatedByMembershipId { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }

    /// <summary>
    /// Opaque. A client stores it and sends it back on the next edit; it never parses, compares or orders by
    /// it.
    /// </summary>
    public required string ConcurrencyToken { get; init; }

    public required IReadOnlyList<TestObservationServiceModel> Observations { get; init; }

    public required IReadOnlyList<TestIssueServiceModel> Issues { get; init; }
}

/// <summary>One note, in the order the tester wants it read.</summary>
public sealed record TestObservationServiceModel
{
    public required Guid Id { get; init; }

    public required TestObservationKind Kind { get; init; }

    public required string Text { get; init; }

    public required int SortOrder { get; init; }
}

/// <summary>One problem the test found, with what was done about it when anything has been.</summary>
public sealed record TestIssueServiceModel
{
    public required Guid Id { get; init; }

    public required TestIssueSeverity Severity { get; init; }

    public required string Title { get; init; }

    public string? Description { get; init; }

    /// <summary>The note this issue was raised from, when it came from one.</summary>
    public Guid? TestObservationId { get; init; }

    public required int SortOrder { get; init; }

    /// <summary>
    /// What was done about it. Null means unresolved — that absence <em>is</em> the state, and there is no
    /// separate flag to disagree with it.
    /// </summary>
    public TestIssueResolutionServiceModel? Resolution { get; init; }
}

/// <summary>What was done about one issue. Write-once: a client may read this but never edit it.</summary>
public sealed record TestIssueResolutionServiceModel
{
    public required Guid Id { get; init; }

    public required TestIssueResolutionKind Kind { get; init; }

    public string? Notes { get; init; }

    public required Guid ResolvedByMembershipId { get; init; }

    public required DateTimeOffset ResolvedAt { get; init; }

    /// <summary>The version carrying the correction, when the resolution named one.</summary>
    public Guid? ResolutionRecipeVersionId { get; init; }

    /// <summary>Why a version at or before the tested one was accepted, when it was.</summary>
    public string? PredatingVersionOverrideReason { get; init; }
}

/// <summary>What a caller is told after resolving one issue.</summary>
/// <remarks>
/// Focused on the issue rather than returning the whole run, because resolving changes nothing else: the run's
/// own fields are untouched and its concurrency token has not moved, so a response carrying them would invite a
/// client to believe it had just been handed a fresh one.
/// </remarks>
public sealed record ResolvedTestIssueServiceModel(
    Guid TestRunId,
    Guid TestIssueId,
    TestIssueResolutionServiceModel Resolution);

/// <summary>
/// Maps a loaded test run to what a caller reads. Ordering is applied here rather than trusted from the
/// database.
/// </summary>
/// <remarks>
/// Sorted by <c>SortOrder</c> explicitly, for the reason the recipe detail mapper sorts its children: a read
/// that happened to come back ordered would make the contract depend on a query plan, and the first reorder
/// nobody noticed would rearrange a creator's notes.
/// </remarks>
public static class RecipeTestRunMapper
{
    public static RecipeTestRunServiceModel ToServiceModel(RecipeTestRun run) => new()
    {
        Id = run.Id,
        RecipeId = run.RecipeId,
        RecipeVersionId = run.RecipeVersionId,
        TestedAt = run.TestedAt,
        TestedByMembershipId = run.TestedByMembershipId,
        Outcome = run.Outcome,
        Rating = run.Rating,
        EnvironmentNotes = run.EnvironmentNotes,
        EquipmentNotes = run.EquipmentNotes,
        SummaryNotes = run.SummaryNotes,
        ActualYieldText = run.ActualYieldText,
        ActualYieldQuantity = run.ActualYieldQuantity,
        ActualYieldUnitId = run.ActualYieldUnitId,
        ActualPrepTimeMinutes = run.ActualPrepTimeMinutes,
        ActualCookTimeMinutes = run.ActualCookTimeMinutes,
        ActualRestTimeMinutes = run.ActualRestTimeMinutes,
        ActualTotalTimeMinutes = run.ActualTotalTimeMinutes,
        CreatedByMembershipId = run.CreatedByMembershipId,
        UpdatedByMembershipId = run.UpdatedByMembershipId,
        CreatedAt = run.CreatedAt,
        UpdatedAt = run.UpdatedAt,
        ConcurrencyToken = RecipeConcurrencyToken.From(run.RowVersion),
        Observations =
        [
            .. run.Observations
                .OrderBy(observation => observation.SortOrder)
                .Select(observation => new TestObservationServiceModel
                {
                    Id = observation.Id,
                    Kind = observation.Kind,
                    Text = observation.Text,
                    SortOrder = observation.SortOrder,
                }),
        ],
        Issues =
        [
            .. run.Issues
                .OrderBy(issue => issue.SortOrder)
                .Select(issue => new TestIssueServiceModel
                {
                    Id = issue.Id,
                    Severity = issue.Severity,
                    Title = issue.Title,
                    Description = issue.Description,
                    TestObservationId = issue.TestObservationId,
                    SortOrder = issue.SortOrder,
                    Resolution = issue.Resolution is null ? null : ToServiceModel(issue.Resolution),
                }),
        ],
    };

    public static TestIssueResolutionServiceModel ToServiceModel(TestIssueResolution resolution) => new()
    {
        Id = resolution.Id,
        Kind = resolution.Kind,
        Notes = resolution.Notes,
        ResolvedByMembershipId = resolution.ResolvedByMembershipId,
        ResolvedAt = resolution.ResolvedAt,
        ResolutionRecipeVersionId = resolution.ResolutionRecipeVersionId,
        PredatingVersionOverrideReason = resolution.PredatingVersionOverrideReason,
    };
}

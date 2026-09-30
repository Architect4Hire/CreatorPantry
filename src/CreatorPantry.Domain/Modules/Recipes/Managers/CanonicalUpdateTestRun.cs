using CreatorPantry.Domain.Managers.Patching;

namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// An update request reduced to what it actually means: the token it was composed against, and the fields it
/// asked to change with their absence preserved.
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="CanonicalCreateTestRun"/> counterpart. The facade hashes it as the idempotency fingerprint
/// and Business reads the request from it, so "same fingerprint" and "same edit" cannot drift apart.
/// </para>
/// <para>
/// <strong>The <see cref="PatchField{T}"/> wrappers survive canonicalization,</strong> which is the whole
/// point: "leave the rating alone" and "clear the rating" are different edits, and a canonical form that
/// flattened them to <c>null</c> would give both the same fingerprint and let a replay of one be answered with
/// the other's result.
/// </para>
/// <para>
/// <strong>The token is part of the fingerprint.</strong> Unlike a create, this edit is meaningful only against
/// one state of the run, so two requests carrying the same changes against different states are different
/// requests — the same reasoning <c>CanonicalRecipePatch</c> applies to an edit.
/// </para>
/// </remarks>
public sealed record CanonicalUpdateTestRun
{
    public required string ExpectedConcurrencyToken { get; init; }

    public PatchField<DateTimeOffset?> TestedAt { get; init; }

    public PatchField<TestRunOutcome?> Outcome { get; init; }

    public PatchField<int?> Rating { get; init; }

    public PatchField<string?> EnvironmentNotes { get; init; }

    public PatchField<string?> EquipmentNotes { get; init; }

    public PatchField<string?> SummaryNotes { get; init; }

    public PatchField<string?> ActualYieldText { get; init; }

    public PatchField<decimal?> ActualYieldQuantity { get; init; }

    public PatchField<Guid?> ActualYieldUnitId { get; init; }

    public PatchField<int?> ActualPrepTimeMinutes { get; init; }

    public PatchField<int?> ActualCookTimeMinutes { get; init; }

    public PatchField<int?> ActualRestTimeMinutes { get; init; }

    public PatchField<int?> ActualTotalTimeMinutes { get; init; }

    /// <summary>The complete desired set of notes, when the body submitted one.</summary>
    public PatchField<IReadOnlyList<CanonicalTestObservationEdit>> Observations { get; init; }

    /// <summary>The complete desired set of issues, when the body submitted one.</summary>
    public PatchField<IReadOnlyList<CanonicalTestIssueEdit>> Issues { get; init; }

    /// <summary>Reduces a validated request to its meaning.</summary>
    /// <remarks>
    /// Strings are trimmed, and an explicitly submitted empty string becomes <c>null</c> — a note sent as
    /// <c>""</c> and one sent as <c>null</c> are both a request to clear it, so treating them alike keeps one
    /// edit from getting two idempotency records. Absence is untouched throughout.
    /// </remarks>
    public static CanonicalUpdateTestRun From(UpdateRecipeTestRunViewModel model) =>
        new()
        {
            ExpectedConcurrencyToken = model.ExpectedConcurrencyToken!,
            TestedAt = model.TestedAt,
            Outcome = model.Outcome,
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
            Observations = model.Observations.IsSubmitted
                ? PatchField<IReadOnlyList<CanonicalTestObservationEdit>>.Submitted(
                    [
                        .. (model.Observations.Value ?? [])
                            .Where(observation => observation is not null)
                            .Select(observation => new CanonicalTestObservationEdit(
                                observation!.Id,
                                observation.Kind ?? TestObservationKind.Unspecified,
                                observation.Text!.Trim())),
                    ])
                : default,
            Issues = model.Issues.IsSubmitted
                ? PatchField<IReadOnlyList<CanonicalTestIssueEdit>>.Submitted(
                    [
                        .. (model.Issues.Value ?? [])
                            .Where(issue => issue is not null)
                            .Select(issue => new CanonicalTestIssueEdit(
                                issue!.Id,
                                issue.Severity!.Value,
                                issue.Title!.Trim(),
                                CleanText(issue.Description),
                                issue.ObservationIndex)),
                    ])
                : default,
        };

    /// <summary>What makes two edits of one test run the same one.</summary>
    /// <param name="testRunId">The run, from the route.</param>
    public object Fingerprint(Guid testRunId) => new
    {
        TestRunId = testRunId,
        ExpectedConcurrencyToken,
        TestedAt = Describe(TestedAt),
        Outcome = Describe(Outcome),
        Rating = Describe(Rating),
        EnvironmentNotes = Describe(EnvironmentNotes),
        EquipmentNotes = Describe(EquipmentNotes),
        SummaryNotes = Describe(SummaryNotes),
        ActualYieldText = Describe(ActualYieldText),
        ActualYieldQuantity = Describe(ActualYieldQuantity),
        ActualYieldUnitId = Describe(ActualYieldUnitId),
        ActualPrepTimeMinutes = Describe(ActualPrepTimeMinutes),
        ActualCookTimeMinutes = Describe(ActualCookTimeMinutes),
        ActualRestTimeMinutes = Describe(ActualRestTimeMinutes),
        ActualTotalTimeMinutes = Describe(ActualTotalTimeMinutes),
        Observations = Describe(Observations),
        Issues = Describe(Issues),
    };

    /// <summary>
    /// One field's contribution to the fingerprint, with absence distinguishable from a submitted
    /// <c>null</c>.
    /// </summary>
    /// <remarks>
    /// The anonymous object a fingerprint is built from is serialized, and a <see cref="PatchField{T}"/> is
    /// deliberately not serializable — its converter refuses to write one, because "absent" has no spelling on
    /// the wire. Projecting to a flag and a value says both things in a form that does.
    /// </remarks>
    private static object? Describe<T>(PatchField<T> field) =>
        field.IsSubmitted ? new { Submitted = true, field.Value } : null;

    private static PatchField<string?> Clean(PatchField<string?> field) =>
        field.IsSubmitted ? PatchField<string?>.Submitted(CleanText(field.Value)) : default;

    private static string? CleanText(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}

/// <summary>One note as an edit describes it: the row it names, or a new one when it names none.</summary>
public sealed record CanonicalTestObservationEdit(Guid? Id, TestObservationKind Kind, string Text);

/// <summary>One issue as an edit describes it.</summary>
public sealed record CanonicalTestIssueEdit(
    Guid? Id, TestIssueSeverity Severity, string Title, string? Description, int? ObservationIndex);

/// <summary>A resolution request reduced to what it means.</summary>
/// <remarks>
/// Carries no actor and no timestamp, for the reason every canonical form here does not: both would make every
/// request unique, which is the opposite of what a fingerprint is for.
/// </remarks>
public sealed record CanonicalResolveTestIssue
{
    public required TestIssueResolutionKind Kind { get; init; }

    public string? Notes { get; init; }

    public int? ResolutionVersionNumber { get; init; }

    public string? PredatingVersionOverrideReason { get; init; }

    public static CanonicalResolveTestIssue From(ResolveTestIssueViewModel model) =>
        new()
        {
            Kind = model.Kind!.Value,
            Notes = Trimmed(model.Notes),
            ResolutionVersionNumber = model.ResolutionVersionNumber,
            PredatingVersionOverrideReason = Trimmed(model.PredatingVersionOverrideReason),
        };

    /// <summary>
    /// What makes two resolution requests the same one.
    /// </summary>
    /// <param name="issueId">The issue, from the route.</param>
    /// <remarks>
    /// The issue is part of it, or one key would cover resolving every issue the test found. In practice the
    /// one-per-issue unique index already makes a second resolution impossible, so this fingerprint's real job
    /// is the narrower one an idempotency record always has: letting a client that lost the response to its
    /// first attempt retry and be told what happened, rather than being told the issue is already resolved by
    /// its own earlier request.
    /// </remarks>
    public object Fingerprint(Guid issueId) => new
    {
        IssueId = issueId,
        Kind,
        Notes,
        ResolutionVersionNumber,
        PredatingVersionOverrideReason,
    };

    private static string? Trimmed(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}

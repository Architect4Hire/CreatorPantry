using CreatorPantry.Domain.Managers.Patching;

namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// The body of <c>PATCH .../recipes/{recipeId}/test-runs/{testRunId}</c>: a creator correcting or finishing
/// the write-up of a test they cooked.
/// </summary>
/// <remarks>
/// <para>
/// A JSON Merge Patch, exactly as <see cref="UpdateRecipeViewModel"/> is: a field the body does not mention is
/// left alone, a field sent with a value is set to it, and a field sent as <c>null</c> is cleared. Every
/// content field is a <see cref="PatchField{T}"/> so those three cases stay distinguishable — the ambiguity a
/// plain nullable collapses is what makes hand-rolled patches silently blank data a client did not know about.
/// </para>
/// <para>
/// <strong>What cannot be changed.</strong> There is no <c>sourceVersionNumber</c>: a test is evidence about
/// the version that was cooked, and repointing it at a different one would not be an edit but a claim that a
/// different thing happened. A test recorded against the wrong version is a test to delete and record again.
/// There is likewise no tester field, for the reason <see cref="CreateRecipeTestRunViewModel"/> gives, and no
/// attachments, because the contract has none anywhere yet.
/// </para>
/// <para>
/// <strong><see cref="Observations"/> and <see cref="Issues"/> replace rather than merge.</strong> A submitted
/// list becomes the run's complete set: entries carrying an id this run owns are updated in place, entries
/// without one are added, and anything the list omits is removed. <c>[]</c> clears them. That is the same
/// contract the recipe's <c>instructions</c> and <c>ingredientGroups</c> carry, and for the same reason —
/// there is no per-row addressing on this route to merge against.
/// </para>
/// <para>
/// <strong>One exception, and it is deliberate:</strong> an issue that has been resolved cannot be dropped by
/// omitting it. A resolution records a decision somebody took about that issue, and removing the issue would
/// erase the decision with it.
/// </para>
/// </remarks>
public sealed record UpdateRecipeTestRunViewModel
{
    /// <summary>
    /// The <c>concurrencyToken</c> from the test run as the caller last read it. Required.
    /// </summary>
    /// <remarks>
    /// Not a field of the run: it describes which state this edit was composed against. Two people writing up
    /// one bake — the cook and whoever is keeping the notes — is the ordinary case here, and the second writer
    /// must be told the run moved rather than silently overwriting the first.
    /// </remarks>
    public string? ExpectedConcurrencyToken { get; init; }

    /// <summary>When the cooking happened. May be changed but not cleared — a test with no date is not one.</summary>
    public PatchField<DateTimeOffset?> TestedAt { get; init; }

    /// <summary>
    /// The tester's verdict. May be changed but not cleared; retracting one is sending
    /// <see cref="TestRunOutcome.NotStated"/> explicitly, which is a statement rather than an absence.
    /// </summary>
    public PatchField<TestRunOutcome?> Outcome { get; init; }

    /// <summary>One to five, or <c>null</c> to withdraw a score.</summary>
    public PatchField<int?> Rating { get; init; }

    public PatchField<string?> EnvironmentNotes { get; init; }

    public PatchField<string?> EquipmentNotes { get; init; }

    public PatchField<string?> SummaryNotes { get; init; }

    public PatchField<string?> ActualYieldText { get; init; }

    public PatchField<decimal?> ActualYieldQuantity { get; init; }

    /// <summary>
    /// The unit the actual yield was measured in. Its dimension is resolved server-side from the catalogue; a
    /// request never says what dimension a unit has.
    /// </summary>
    public PatchField<Guid?> ActualYieldUnitId { get; init; }

    public PatchField<int?> ActualPrepTimeMinutes { get; init; }

    public PatchField<int?> ActualCookTimeMinutes { get; init; }

    public PatchField<int?> ActualRestTimeMinutes { get; init; }

    /// <summary>
    /// What the whole thing took. Stored as sent; nothing sums the three above into it.
    /// </summary>
    public PatchField<int?> ActualTotalTimeMinutes { get; init; }

    /// <inheritdoc cref="UpdateRecipeTestRunViewModel"/>
    public PatchField<IReadOnlyList<TestObservationInputViewModel?>?> Observations { get; init; }

    /// <inheritdoc cref="UpdateRecipeTestRunViewModel"/>
    public PatchField<IReadOnlyList<TestIssueInputViewModel?>?> Issues { get; init; }
}

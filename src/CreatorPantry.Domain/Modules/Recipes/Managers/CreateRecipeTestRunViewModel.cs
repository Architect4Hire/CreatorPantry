namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// The body of <c>POST /api/v1/workspaces/{workspaceSlug}/recipes/{recipeId}/test-runs</c>: a creator
/// recording one cook of one exact version of their recipe.
/// </summary>
/// <remarks>
/// <para>
/// <strong><see cref="SourceVersionNumber"/> is required</strong>, unlike the duplicate route's, which may be
/// omitted to mean "as it currently stands". A test is evidence about a specific set of words, so there is no
/// defensible reading of "whichever version is current when this arrives" — the recipe may have changed
/// between the cook and the write-up, and a run that recorded the wrong one would be worse than no record.
/// </para>
/// <para>
/// <strong>No concurrency token, for the reason <see cref="DuplicateRecipeViewModel"/> gives:</strong> nothing
/// is being overwritten. This writes a new test run beside the recipe and changes no recipe field, so there is
/// no prior state the request was composed against and no conflict to report.
/// </para>
/// <para>
/// <strong>No attachments, and that is deliberate rather than unfinished.</strong> A media asset id supplied
/// by a client can only be trusted once something can authorize it against the resolved workspace, and the
/// media aggregate does not exist yet — which is why <c>TestAttachmentLink.MediaAssetId</c> carries no foreign
/// key. The recipe write seam reached the same conclusion first: <see cref="CreateRecipeViewModel"/> and
/// <see cref="UpdateRecipeViewModel"/> accept no asset field either, although a recipe's links are published
/// on the way out. Attaching test media arrives with the media facade that can govern it.
/// </para>
/// <para>
/// <strong>No tester field.</strong> The run is recorded as tested by the authenticated caller. The entity
/// keeps the tester and the author separate because one person writing up another's bake is ordinary, but
/// letting a request name the tester means accepting a workspace-scoped identity from a client, and that is a
/// decision about who may appear in a workspace's records rather than a field to add.
/// </para>
/// </remarks>
public sealed record CreateRecipeTestRunViewModel
{
    /// <summary>
    /// Which version was cooked, by number as the history lists it. Required.
    /// </summary>
    /// <remarks>
    /// A version <em>number</em> rather than an id, matching the comparison, restore and duplicate routes: a
    /// number is what a creator reads in a history and cites, and it is unique within its recipe, so a number
    /// naming another recipe's version matches nothing here rather than being refused by a check.
    /// </remarks>
    public int? SourceVersionNumber { get; init; }

    /// <summary>
    /// When the cooking happened. Required, and not the same as when this was submitted.
    /// </summary>
    /// <remarks>
    /// Testers write their notes up afterwards, sometimes days afterwards, and a history ordered by when
    /// somebody typed it is not a history of the cooking. A value in the future is refused below, because a
    /// mistyped year would sit at the top of a recipe's history permanently.
    /// </remarks>
    public DateTimeOffset? TestedAt { get; init; }

    /// <summary>The tester's verdict. Optional; absent means <see cref="TestRunOutcome.NotStated"/>.</summary>
    public TestRunOutcome? Outcome { get; init; }

    /// <summary>One to five, or absent when the tester did not score it.</summary>
    public int? Rating { get; init; }

    /// <summary>The conditions the test ran under: oven, altitude, weather.</summary>
    public string? EnvironmentNotes { get; init; }

    /// <summary>What was actually used, where it differed from what the recipe calls for.</summary>
    public string? EquipmentNotes { get; init; }

    /// <summary>How the cook went overall, as opposed to an observation about one aspect of it.</summary>
    public string? SummaryNotes { get; init; }

    /// <summary>What it actually made, in the tester's own words: "got 10, not 12".</summary>
    public string? ActualYieldText { get; init; }

    /// <summary>The numeric actual yield, when one was measured.</summary>
    public decimal? ActualYieldQuantity { get; init; }

    /// <summary>
    /// The unit the actual yield was measured in. Its dimension is resolved server-side from the catalogue,
    /// exactly as <see cref="CreateRecipeViewModel.YieldUnitId"/>'s is — a request does not get to say what
    /// dimension a unit has.
    /// </summary>
    public Guid? ActualYieldUnitId { get; init; }

    /// <summary>What the prep actually took, in minutes.</summary>
    public int? ActualPrepTimeMinutes { get; init; }

    /// <summary>What the cooking actually took, in minutes.</summary>
    public int? ActualCookTimeMinutes { get; init; }

    /// <summary>What the resting actually took, in minutes.</summary>
    public int? ActualRestTimeMinutes { get; init; }

    /// <summary>
    /// What the whole thing actually took. Stored as sent and never derived from the three above — a real
    /// kitchen overlaps prep with cooking, so a total below their sum is a measurement rather than a mistake.
    /// </summary>
    public int? ActualTotalTimeMinutes { get; init; }

    /// <summary>What the tester noticed, in the order they want it read.</summary>
    public IReadOnlyList<TestObservationInputViewModel?>? Observations { get; init; }

    /// <summary>What the test found wrong with the recipe, in the order they want it read.</summary>
    public IReadOnlyList<TestIssueInputViewModel?>? Issues { get; init; }
}

/// <summary>One note in a submitted test run. Position in the list is its order.</summary>
public sealed record TestObservationInputViewModel
{
    /// <summary>
    /// The note to change, when editing an existing test. Absent on a create, and absent on an update for a
    /// note being added.
    /// </summary>
    /// <remarks>
    /// On a create nothing exists for it to name, so a submitted id is a client bug and is refused as one. On
    /// an update an id this run does not own is treated as no id at all — a new note — for the reason
    /// <c>ReconcileInstructions</c> gives: distinguishing "unknown" from "belongs to another workspace" is the
    /// disclosure tenancy.md forbids, and the outcome is identical either way.
    /// </remarks>
    public Guid? Id { get; init; }

    /// <summary>What the note is about. Optional; absent means <see cref="TestObservationKind.Unspecified"/>.</summary>
    public TestObservationKind? Kind { get; init; }

    /// <summary>What the tester noticed. Required — an observation with nothing in it is not one.</summary>
    public string? Text { get; init; }
}

/// <summary>One problem found by a submitted test run. Position in the list is its order.</summary>
public sealed record TestIssueInputViewModel
{
    /// <inheritdoc cref="TestObservationInputViewModel.Id"/>
    public Guid? Id { get; init; }

    /// <summary>How badly it affects the recipe. Required; there is no safe default (<see cref="TestIssueSeverity"/>).</summary>
    public TestIssueSeverity? Severity { get; init; }

    /// <summary>The problem in one line. Required.</summary>
    public string? Title { get; init; }

    /// <summary>The problem at length, when one line is not enough.</summary>
    public string? Description { get; init; }

    /// <summary>
    /// The observation this issue came from, as a position in this request's own
    /// <see cref="CreateRecipeTestRunViewModel.Observations"/> list. Optional.
    /// </summary>
    /// <remarks>
    /// An index rather than an id because the observations it points at are being created by this same
    /// request and have no ids yet. The alternative — two round trips, one to create the notes and one to
    /// file the issues against them — would make a half-recorded test a reachable state for no gain.
    /// </remarks>
    public int? ObservationIndex { get; init; }
}

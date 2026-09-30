namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// One recorded test as a history screen reads it: enough to see how the cook went and decide which test to
/// open, and none of the material inside it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A separate shape from <see cref="RecipeTestRunServiceModel"/>, not a subset of it.</strong> That model
/// is one whole test — every note, every observation, every issue and its resolution — returned by the write
/// seams to whoever just changed it. This is a row in a list, and the difference is the restriction TESTRUN-003
/// carries: no attachment byte, no asset reference, no recipe snapshot, and no child text. Widening the other
/// model to serve both would mean a list route deciding, per request, how much of a creator's writing to fetch.
/// </para>
/// <para>
/// <strong>Counts where the full model has collections.</strong> Three numbers say what a creator needs from a
/// list — how much was noted, how much went wrong, how much is still open — and none of them is a sentence
/// somebody wrote. Opening the test is what reads the sentences.
/// </para>
/// <para>
/// <strong><see cref="SummaryNotes"/> is the one exception</strong>, and the reasoning is
/// <see cref="RecipeSummaryServiceModel"/>'s: one bounded column, already on the row being read, answering the
/// question a list is actually scanned for. <c>environmentNotes</c> and <c>equipmentNotes</c> are conditions
/// rather than results and stay behind.
/// </para>
/// <para>
/// <strong>The tester is both an id and a name</strong>, which no other list in this module does. The id because
/// <see cref="RecipeTestRunServiceModel.TestedByMembershipId"/> already publishes it and
/// <c>?testedBy=</c> has to be populable from what a client holds; the name because an id names nobody a creator
/// can read. <see cref="RecipeSummaryServiceModel"/> publishes neither, and states why: a recipe library has no
/// members endpoint behind it. This route's own rows are that source.
/// </para>
/// </remarks>
/// <param name="RecipeVersionId">
/// The exact version that was cooked. Published beside <paramref name="VersionNumber"/> rather than instead of it
/// because the two answer different questions: the number is what a creator cites, the id is what a client
/// follows to read that version.
/// </param>
/// <param name="VersionNumber">The number of that version, as the recipe's history lists it.</param>
/// <param name="Outcome">
/// The tester's verdict, and only that. Never derived from <paramref name="IssueCount"/> or
/// <paramref name="Rating"/> — a tester may call a bake a success with three things to fix, and the model keeps
/// their word for it.
/// </param>
/// <param name="UnresolvedIssueCount">
/// How many of <paramref name="IssueCount"/> have nothing recorded about what was done. A statement about the
/// tester's own record and <strong>never a readiness, safety, allergen or dietary finding</strong> — see
/// <see cref="TestRunIssueFilter"/>.
/// </param>
/// <param name="AttachmentCount">
/// How many assets the test links. A number and nothing else: no id, no URL, no bytes. Authorizing an asset
/// belongs to the media library, and until it arrives this is the most the route can honestly say.
/// </param>
/// <param name="ConcurrencyToken">
/// Opaque, and the token an edit of this test must quote. Carried on a summary because no route reads one test on
/// its own yet, so without it the only way to obtain a token would be the response to a previous edit. A client
/// stores it and sends it back; it never parses, compares or orders by it.
/// </param>
public sealed record TestRunSummaryServiceModel
{
    public required Guid Id { get; init; }

    public required Guid RecipeVersionId { get; init; }

    public required int VersionNumber { get; init; }

    /// <summary>When the cooking happened, which is not when the record was written.</summary>
    public required DateTimeOffset TestedAt { get; init; }

    /// <summary>The membership of whoever cooked it, not their Identity user id. What <c>?testedBy=</c> accepts.</summary>
    public required Guid TestedByMembershipId { get; init; }

    /// <summary>
    /// The display name of whoever cooked it, or <c>null</c> when the membership behind it can no longer be named.
    /// </summary>
    /// <remarks>
    /// Resolved by the Facade through the Tenancy facade, which is the only way across: memberships belong to that
    /// module and display names to Auth beyond it. <strong>Nullable, and it will happen</strong> — a test outlives
    /// the membership that cooked it by design, and the honest answer then is that the test exists and its tester
    /// cannot be named, not that the history is broken.
    /// </remarks>
    public required string? TestedByName { get; init; }

    public required TestRunOutcome Outcome { get; init; }

    /// <summary>One to five, or <c>null</c> when the tester did not score it.</summary>
    public int? Rating { get; init; }

    /// <summary>How the cook went overall, in the tester's words.</summary>
    public string? SummaryNotes { get; init; }

    /// <summary>What it actually made, exactly as the tester phrased it. The canonical actual yield.</summary>
    public string? ActualYieldText { get; init; }

    public decimal? ActualYieldQuantity { get; init; }

    public Guid? ActualYieldUnitId { get; init; }

    public int? ActualPrepTimeMinutes { get; init; }

    public int? ActualCookTimeMinutes { get; init; }

    public int? ActualRestTimeMinutes { get; init; }

    /// <summary>What the whole thing took, as recorded. Never the sum of the three above.</summary>
    public int? ActualTotalTimeMinutes { get; init; }

    public required int ObservationCount { get; init; }

    public required int IssueCount { get; init; }

    public required int UnresolvedIssueCount { get; init; }

    public required int AttachmentCount { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }

    public required string ConcurrencyToken { get; init; }
}

/// <summary>
/// How the testing has gone across everything the filters selected.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Over the filtered set, not the whole recipe.</strong> That is what makes
/// <see cref="TotalCount"/> the paging total as well as a headline figure, and it means narrowing the filters
/// visibly narrows these numbers — which is the behaviour a creator expects of a summary sitting above a list they
/// are filtering. A summary of every test regardless of filters would be a second, unrelated query whose figures
/// never matched the rows beneath it.
/// </para>
/// <para>
/// <strong>Two statements, not one, and neither is windowed.</strong> Like
/// <see cref="RecipeSearchPageServiceModel.TotalCount"/>, these are read beside the page rather than within it, so
/// under a concurrent write they can disagree with <see cref="TestRunHistoryPageServiceModel.Items"/> by however
/// many tests were recorded in between. That is the accepted cost of not paying for a windowed count on every
/// page; a creator seeing "12 tests" briefly read "11" is not a defect worth that price.
/// </para>
/// <para>
/// <strong>Nothing here is a readiness verdict.</strong> These are counts of what testers wrote down. Whether a
/// recipe is ready is TESTRUN-004's deterministic evaluation, which reads far more than this and explains itself
/// rule by rule; a screen that added these up and called the answer "ready" would be inventing a conclusion.
/// </para>
/// </remarks>
/// <param name="TotalCount">
/// How many tests match the filters across every page. A count of rows, never a page number — the ordering is a
/// keyset, and there is no page N to jump to.
/// </param>
/// <param name="ByOutcome">
/// How many held each verdict, keyed by outcome name. <strong>Every outcome is always present</strong>, at zero
/// when nothing matched, so a screen never has to tell "none" from "not counted". A dictionary rather than four
/// fields so that adding an outcome stays additive.
/// </param>
/// <param name="RunsWithUnresolvedIssues">
/// How many of those tests still have at least one issue with no resolution recorded.
/// </param>
/// <param name="UnresolvedIssueCount">
/// How many such issues there are in total. The more useful of the two figures: one bake with four outstanding
/// problems and four bakes with one each read identically on the left.
/// </param>
public sealed record TestRunHistorySummaryServiceModel(
    int TotalCount,
    IReadOnlyDictionary<TestRunOutcome, int> ByOutcome,
    int RunsWithUnresolvedIssues,
    int UnresolvedIssueCount);

/// <summary>
/// One page of a recipe's test history: the rows, where to resume, and how the testing has gone overall.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately <em>not</em> the shared <c>CursorPageServiceModel&lt;T&gt;</c>, which is the published
/// <c>CursorPage</c> component and carries exactly two fields — the same decision, for the same reason, as
/// <see cref="RecipeSearchPageServiceModel"/>: adding a third to that envelope would change a shape already
/// shipped on every reference route, none of which has a summary to report.
/// </para>
/// <para>
/// <see cref="NextCursor"/> is <c>null</c> on the last page, so a client loops until it is null rather than
/// comparing counts against a page size the server may have clamped.
/// </para>
/// </remarks>
/// <param name="Summary">
/// The counts, or <c>null</c> when the caller passed <c>includeSummary=false</c>. Null means "not asked for" and
/// never "nothing to report" — a recipe with no tests has a summary of zeros.
/// </param>
public sealed record TestRunHistoryPageServiceModel(
    IReadOnlyList<TestRunSummaryServiceModel> Items,
    string? NextCursor,
    TestRunHistorySummaryServiceModel? Summary);

/// <summary>
/// A page of test history as Business can build it, and the tester names it cannot resolve on its own.
/// </summary>
/// <remarks>
/// <para>
/// The same structural split <see cref="RecipeVersionHistoryPageResult"/> exists for, and for the same reason:
/// naming a tester means reading a membership and the user behind it — two modules Business may not call, since it
/// calls its own DataLayer and nothing else. The Facade may, and does. So Business builds everything it can and
/// hands over the ids it could not spend.
/// </para>
/// <para>
/// <strong>Keyed by test-run id, not by position.</strong> The two collections could have been left to line up
/// index by index, and would have, until the day something reordered or filtered one of them.
/// </para>
/// </remarks>
/// <param name="Page">The page, with every row's <c>TestedByName</c> still null.</param>
/// <param name="TesterMembershipByTestRunId">The membership that cooked each test on this page.</param>
public sealed record TestRunHistoryPageResult(
    TestRunHistoryPageServiceModel Page,
    IReadOnlyDictionary<Guid, Guid> TesterMembershipByTestRunId);

using System.Globalization;
using CreatorPantry.Domain.Managers.Paging;

namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// What a creator is narrowing one recipe's test history down to. Every member is optional, and an empty
/// instance means "every test of this recipe".
/// </summary>
/// <remarks>
/// <para>
/// <strong>There is no workspace member, and there must not be.</strong> The workspace is resolved from the
/// route and the caller's membership and reaches the query through the global EF Core query filter; a field here
/// would be one a request body, a job payload or an AI tool argument could set (tenancy.md).
/// </para>
/// <para>
/// <strong>There is no recipe member either</strong>, unlike <see cref="RecipeSearchFilters"/>, which has no
/// enclosing resource to belong to. The recipe is a route segment and travels on
/// <see cref="TestRunHistoryCriteria.RecipeId"/> — it scopes the read rather than filtering within it, and a
/// filter a caller could clear is not the same thing as the resource they asked about.
/// </para>
/// <para>
/// The collections are AND-ed against each other and OR-ed within themselves: two outcomes and one tester means
/// "in either outcome, and by that tester". An empty collection is the absence of a filter rather than a filter
/// matching nothing, which is what a client that cleared one meant.
/// </para>
/// </remarks>
/// <param name="VersionNumbers">
/// Versions to include, <strong>by number</strong> rather than by id. Numbers are what a creator cites and what
/// <see cref="CreateRecipeTestRunViewModel.SourceVersionNumber"/> already accepts, and a number is unique only
/// within its recipe — which is exactly the scope this read already has.
/// </param>
/// <param name="TesterMembershipIds">
/// Testers, by <c>WorkspaceMembership</c> id, read from
/// <see cref="Data.Entities.RecipeTestRun.TestedByMembershipId"/> — whoever <em>cooked</em> it, not whoever typed
/// it up. The two are deliberately separate columns, and "whose test is this" is a question about the kitchen.
/// <para>
/// An id-valued filter here where <see cref="RecipeSearchFilters.CreatorMembershipIds"/> is reachable only
/// through <c>mine</c>, and the difference is what each route publishes. A library card carries no author, so an
/// id-valued filter there would be one nobody could populate; this route publishes
/// <see cref="TestRunSummaryServiceModel.TestedByMembershipId"/> on every row, so the ids it accepts are exactly
/// the ids it hands out.
/// </para>
/// </param>
/// <param name="Outcomes">Verdicts to include. The tester's own judgement, never derived from the issues.</param>
/// <param name="TestedOnOrAfter">Inclusive lower bound on when the cooking happened.</param>
/// <param name="TestedBefore">
/// Exclusive upper bound on when the cooking happened. Half-open so a caller can ask for a day, a week or a month
/// without knowing the precision the column stores, and so adjacent ranges neither overlap nor gap — the same
/// rule <see cref="RecipeSearchFilters.UpdatedBefore"/> follows.
/// <para>
/// Bounds <c>TestedAt</c> and never <c>CreatedAt</c>, because the ordering does too: a history filtered by when
/// somebody typed their notes up would exclude the bake they wrote up a week late.
/// </para>
/// </param>
/// <param name="Issues">
/// Whether the run still has an issue nobody has decided anything about. See <see cref="TestRunIssueFilter"/>.
/// </param>
public sealed record TestRunHistoryFilters(
    IReadOnlyList<int>? VersionNumbers = null,
    IReadOnlyList<Guid>? TesterMembershipIds = null,
    IReadOnlyList<TestRunOutcome>? Outcomes = null,
    DateTimeOffset? TestedOnOrAfter = null,
    DateTimeOffset? TestedBefore = null,
    TestRunIssueFilter? Issues = null);

/// <summary>
/// The position a page of test history resumes from: the last row's tested time and its id.
/// </summary>
/// <remarks>
/// <para>
/// Two parts rather than one, unlike <see cref="RecipeVersionHistoryPosition"/>. A recipe's versions are ordered
/// by a number a unique index makes total; its tests are ordered by a timestamp two testers can share — two
/// bakes on the same morning, or a batch of notes written up with the same recorded time — so the ordering is not
/// total without the tie-break, and a page boundary falling between two tied rows would repeat one and skip the
/// other on every read.
/// </para>
/// <para>
/// The tie-breaker is the run's id, which is what the index carries. A GUID is ordered differently by SQL Server
/// than by C#, which <see cref="ReferenceCursor"/> notes and which is harmless for the reason
/// <see cref="RecipeSearchPosition"/> gives at length: the <c>WHERE</c> and the <c>ORDER BY</c> are evaluated by
/// the same database, so they agree with each other, which is all a keyset needs. A cursor is therefore not
/// portable between engines, exactly as every other one here is not.
/// </para>
/// </remarks>
public sealed record TestRunHistoryPosition
{
    /// <summary>
    /// Round-trip ("O"), so the encoded value carries every digit of precision the column holds — the same
    /// choice, for the same reason, as <see cref="RecipeSearchPosition"/>. A format that truncated would produce
    /// a cursor that resumes slightly before where it claims, quietly repeating rows.
    /// </summary>
    private const string TimestampFormat = "O";

    private TestRunHistoryPosition(DateTimeOffset testedAt, Guid testRunId)
    {
        TestedAt = testedAt;
        TestRunId = testRunId;
    }

    /// <summary>The last row's tested time. The page resumes strictly below it.</summary>
    public DateTimeOffset TestedAt { get; }

    /// <summary>The last row's id, which breaks every tie the timestamp leaves.</summary>
    public Guid TestRunId { get; }

    /// <summary>
    /// Converts a decoded cursor into a position, or returns <c>false</c> when it does not describe one.
    /// </summary>
    /// <remarks>
    /// Returns <c>false</c> rather than throwing, for the reason every other position type here does: the two
    /// halves arrived from a query string, so a cursor whose sort value is not a timestamp is a caller's mistake
    /// and belongs in a refusal rather than in an exception from a query. Whether the cursor was issued for this
    /// workspace and this recipe is a different question, already settled by
    /// <see cref="ReferenceCursor.TryResolve"/> against the scope.
    /// </remarks>
    public static bool TryCreate(ReferenceCursor cursor, out TestRunHistoryPosition? position)
    {
        position = null;

        if (!Guid.TryParseExact(cursor.TieBreaker, "D", out var testRunId)
            || !TryParseTimestamp(cursor.SortValue, out var testedAt))
        {
            return false;
        }

        position = new TestRunHistoryPosition(testedAt, testRunId);

        return true;
    }

    /// <summary>
    /// How a tested time is written into a cursor. Paired with <see cref="TryParseTimestamp"/> and used by
    /// <see cref="TestRunHistoryRecord.SortValue"/>, so the value a page mints and the value the next page parses
    /// cannot drift into two spellings.
    /// </summary>
    internal static string FormatTimestamp(DateTimeOffset value) =>
        value.ToString(TimestampFormat, CultureInfo.InvariantCulture);

    private static bool TryParseTimestamp(string value, out DateTimeOffset parsed) =>
        DateTimeOffset.TryParseExact(
            value,
            TimestampFormat,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out parsed);
}

/// <summary>
/// One request for a page of one recipe's test history: which recipe, what to include, where to resume, how many
/// rows, and whether to count.
/// </summary>
/// <remarks>
/// <para>
/// <strong>There is no sort.</strong> Most recently cooked first is the contract, not a preference — a test
/// history is read backwards from the last bake — and the ordering has an index built for it exactly
/// (<c>IX_RecipeTestRuns_Workspace_Recipe_TestedAt</c>). A second ordering would mean a second index, a second
/// cursor shape and a second thing to explain, which is the judgement <see cref="RecipeVersionHistoryCriteria"/>
/// already made for the version history.
/// </para>
/// <para>
/// <strong>An unclamped page size is unrepresentable</strong>, exactly as on <see cref="RecipeSearchCriteria"/>:
/// <see cref="Limit"/> is derived rather than stored, so no constructor or <c>with</c> expression can produce a
/// criteria asking for ten thousand rows.
/// </para>
/// </remarks>
/// <param name="RecipeId">The recipe whose tests these are, resolved from the route.</param>
/// <param name="Filters">What to include. An empty instance means every test of that recipe.</param>
/// <param name="Scope">
/// Identifies the ordered set these filters select, for this recipe, in the resolved workspace. Cursors minted
/// for a page of it are bound to this, so one cannot be replayed against another recipe, another workspace or
/// another set of filters. Built by <see cref="TestRunHistoryScope"/>; the repository ignores it.
/// </param>
/// <param name="Position">Where to resume, or <c>null</c> for the first page.</param>
/// <param name="RequestedLimit">
/// The page size asked for, or <c>null</c> for the default. Out-of-range values are clamped rather than rejected,
/// so a client cannot fail a read by asking for too much.
/// </param>
/// <param name="IncludeSummary">
/// Whether to build the counts alongside the page. Defaults to <c>true</c>, because a test-kitchen screen opens
/// by saying how the testing has gone overall; a caller following a cursor already has the figures and should
/// turn them off rather than pay for two more statements on every page.
/// </param>
public sealed record TestRunHistoryCriteria(
    Guid RecipeId,
    TestRunHistoryFilters Filters,
    string Scope,
    TestRunHistoryPosition? Position = null,
    int? RequestedLimit = null,
    bool IncludeSummary = true)
{
    /// <inheritdoc cref="RecipeSearchCriteria.Limit"/>
    public int Limit => ReferencePolicy.ClampPageSize(RequestedLimit);
}

/// <summary>
/// One row of a recipe's test history, as the repository projects it: what the test says about itself, how much
/// it holds, and none of what it holds.
/// </summary>
/// <remarks>
/// <para>
/// Projected entirely in SQL from <c>RecipeTestRuns</c>. <strong>No attachment byte and no asset field is read,
/// and no version snapshot is touched</strong> — <c>TestAttachmentLinks</c> is reached only to be counted, and
/// <c>RecipeVersionSnapshots</c> is never named. That is TESTRUN-003's restriction expressed as a shape: there is
/// nowhere in this record for either to arrive.
/// </para>
/// <para>
/// <strong>Observation and issue text is absent, replaced by counts.</strong> A hundred runs each carrying fifty
/// observations would be five thousand rows of creator prose fetched to render a list nobody reads it in.
/// <see cref="SummaryNotes"/> is the one prose field kept, on the same terms as
/// <see cref="RecipeSummaryRecord.Description"/>: one bounded column, on the row already being read, saying how
/// the cook went overall. <c>EnvironmentNotes</c> and <c>EquipmentNotes</c> are left for a reader of the run
/// itself.
/// </para>
/// <para>
/// <strong>The membership column is read and published.</strong> Unlike <see cref="RecipeSummaryRecord"/>, whose
/// membership ids stop at Business, this one reaches the wire — <see cref="RecipeTestRunServiceModel"/> already
/// publishes it, so withholding it here would mean one feature answering two ways, and the tester filter would
/// accept an id no route hands out. The <em>name</em> beside it is resolved by the Facade through the Tenancy
/// facade, because this module can read neither memberships nor user names.
/// </para>
/// </remarks>
/// <param name="VersionNumber">
/// The number of the version that was cooked, read from <c>RecipeVersions</c> by the run's own version id.
/// Non-nullable: <see cref="Data.Entities.RecipeTestRun.RecipeVersionId"/> is required and constrained by a
/// composite foreign key, so the version a run names exists by definition.
/// </param>
/// <param name="UnresolvedIssueCount">
/// How many of <paramref name="IssueCount"/> have no resolution recorded. Present on every row rather than only
/// when filtered on, because a creator cannot act on a filter whose result they cannot see — the same reasoning
/// as <see cref="RecipeSummaryRecord.HasUnmatchedIngredients"/>.
/// </param>
/// <param name="AttachmentCount">
/// How many assets the run links. <strong>A count, never a link and never a byte</strong>: authorizing an asset
/// belongs to the media library, which has not arrived, and a number is the most this route can honestly say
/// about photographs it cannot authorize.
/// </param>
/// <param name="RowVersion">
/// The run's concurrency token, carried so the published row can quote it. Eight bytes, and the reason
/// <see cref="TestRunSummaryServiceModel.ConcurrencyToken"/> exists.
/// </param>
public sealed record TestRunHistoryRecord(
    Guid Id,
    Guid RecipeVersionId,
    int VersionNumber,
    DateTimeOffset TestedAt,
    Guid TestedByMembershipId,
    TestRunOutcome Outcome,
    int? Rating,
    string? SummaryNotes,
    string? ActualYieldText,
    decimal? ActualYieldQuantity,
    Guid? ActualYieldUnitId,
    int? ActualPrepTimeMinutes,
    int? ActualCookTimeMinutes,
    int? ActualRestTimeMinutes,
    int? ActualTotalTimeMinutes,
    int ObservationCount,
    int IssueCount,
    int UnresolvedIssueCount,
    int AttachmentCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    byte[] RowVersion) : IReferenceRow
{
    /// <inheritdoc />
    public string SortValue => TestRunHistoryPosition.FormatTimestamp(TestedAt);

    /// <inheritdoc />
    /// <remarks>The run's id, formatted the way <see cref="TestRunHistoryPosition.TryCreate"/> parses it.</remarks>
    public string TieBreaker => Id.ToString("D");
}

/// <summary>
/// The counts a repository can produce about a filtered set of test runs, before anything has been turned into a
/// published shape.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Over the filtered set, not the whole recipe.</strong> The outcome counts summing to the number of runs
/// the page is drawn from is what makes <see cref="TestRunHistorySummaryServiceModel.TotalCount"/> a paging total
/// rather than a second, unrelated number; a summary that ignored the filters would disagree with the list
/// beneath it and could not be used to page.
/// </para>
/// <para>
/// <strong>Outcomes that no run matched are absent rather than present as zero.</strong> The mapper fills them
/// in, so the published object always names every outcome — a screen must not have to tell "none" from "not
/// counted" — but a <c>GROUP BY</c> cannot invent a row for a value nothing has, and pretending otherwise here
/// would have this record claim knowledge the statement did not return.
/// </para>
/// </remarks>
/// <param name="OutcomeCounts">How many runs held each verdict, for the verdicts at least one run held.</param>
/// <param name="RunsWithUnresolvedIssues">How many runs have at least one issue with no resolution.</param>
/// <param name="UnresolvedIssueCount">
/// How many such issues there are in total across those runs. A different number from the one above and usually
/// the more useful of the two: one bake with four outstanding problems and four bakes with one each are the same
/// figure on the left and very different situations.
/// </param>
public sealed record TestRunHistoryCounts(
    IReadOnlyDictionary<TestRunOutcome, int> OutcomeCounts,
    int RunsWithUnresolvedIssues,
    int UnresolvedIssueCount);

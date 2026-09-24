using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Modules.Recipes.Data;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Recipes;

/// <summary>
/// The version history query against a real SQL Server: that the keyset predicate and the <c>ORDER BY</c>
/// agree, that the projection translates at all, that the archive is never touched, and that the workspace
/// filter holds on a table read by recipe id alone.
/// </summary>
/// <remarks>
/// Each test starts from an empty recipe table. Deleting is done in SQL because <c>RecipeVersion</c> is an
/// immutable record and <c>ImmutableRecordInterceptor</c> refuses to delete one through the change tracker —
/// correctly, and the alternative would be a test that could not clean up after itself.
/// </remarks>
public sealed class RecipeVersionHistoryRepositoryTests(SqlServerRecipeFixture fixture)
    : IClassFixture<SqlServerRecipeFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = SqlServerRecipeFixture.Now;

    /// <inheritdoc cref="RecipeSearchRepositoryTests" />
    private const string TestScope = "t";

    public async ValueTask InitializeAsync()
    {
        await using var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);

        await SqlServerRecipeFixture.Db(scope).Database.ExecuteSqlRawAsync(
            """
            DELETE FROM RecipeVersionSnapshots;
            DELETE FROM RecipeVersions;
            DELETE FROM Recipes;
            """,
            TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // --- Ordering and content ----------------------------------------------------------------------------

    [Fact]
    public async Task A_history_comes_back_highest_version_number_first()
    {
        var recipeId = await AddAsync(versions: 4);

        var (rows, hasMore) = await PageAsync(recipeId, limit: 25);

        Assert.Equal([4, 3, 2, 1], rows.Select(row => row.VersionNumber));
        Assert.False(hasMore);
    }

    /// <summary>
    /// Ordered by number rather than by timestamp, which is the difference that shows when two versions share
    /// an instant — as two writes in the same tick can.
    /// </summary>
    [Fact]
    public async Task Versions_sharing_a_timestamp_still_come_back_in_number_order()
    {
        var recipeId = await AddAsync(versions: 3, sameInstant: true);

        var (rows, _) = await PageAsync(recipeId, limit: 25);

        Assert.Equal([3, 2, 1], rows.Select(row => row.VersionNumber));
    }

    [Fact]
    public async Task Each_row_carries_its_lineage_and_its_proposal_link()
    {
        var proposalId = Guid.NewGuid();
        var recipeId = await AddAsync(versions: 3, proposalOnVersion: (2, proposalId));

        var (rows, _) = await PageAsync(recipeId, limit: 25);

        // Newest first, so rows[2] is version 1 — the only one with no parent.
        Assert.Null(rows[2].ParentVersionId);
        Assert.Equal(rows[2].Id, rows[1].ParentVersionId);
        Assert.Equal(rows[1].Id, rows[0].ParentVersionId);

        var fromProposal = rows.Single(row => row.VersionNumber == 2);

        Assert.Equal(RecipeVersionSource.AiProposalAccepted, fromProposal.Source);
        Assert.Equal(proposalId, fromProposal.AiProposalId);
        Assert.All(
            rows.Where(row => row.VersionNumber != 2),
            row => Assert.Null(row.AiProposalId));
    }

    /// <summary>
    /// The restriction, proved where it can actually be proved. A projection that joined the archive and threw
    /// it away would return identical records, so the rows cannot show this — only the statement can.
    /// </summary>
    [Fact]
    public async Task Listing_a_history_never_reads_the_snapshot_table()
    {
        var recipeId = await AddAsync(versions: 3, withSnapshots: true);

        var capture = new CaptureCommandText();
        await using var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA, capture);

        var (rows, _) = await Versions(scope).ListHistoryAsync(
            new RecipeVersionHistoryCriteria(recipeId, TestScope, RequestedLimit: 25),
            TestContext.Current.CancellationToken);

        Assert.Equal(3, rows.Count);
        Assert.NotEmpty(capture.Commands);
        Assert.False(capture.Mentions("RecipeVersionSnapshots"));

        // The columns that exist but are not published either. Their absence from the SQL is what makes
        // "metadata only" a property of the query rather than of the mapping above it.
        Assert.False(capture.Mentions("BasedOnRecipeRowVersion"));
        Assert.False(capture.Mentions("SnapshotSchemaVersion"));
    }

    // --- Paging ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Following_the_cursor_walks_the_whole_history_once_and_terminates()
    {
        var recipeId = await AddAsync(versions: 7);

        var seen = await DrainAsync(recipeId, limit: 2);

        // Every version once, still descending across four page boundaries.
        Assert.Equal([7, 6, 5, 4, 3, 2, 1], seen.Select(row => row.VersionNumber));
    }

    [Fact]
    public async Task A_page_reports_more_only_when_more_follows()
    {
        var recipeId = await AddAsync(versions: 3);

        var (first, hasMore) = await PageAsync(recipeId, limit: 3);

        // Exactly the limit, and nothing after it: the extra row fetched is how this is known without a second
        // statement, and it must not be returned.
        Assert.Equal(3, first.Count);
        Assert.False(hasMore);
    }

    [Fact]
    public async Task A_position_resumes_strictly_below_the_last_row_returned()
    {
        var recipeId = await AddAsync(versions: 5);

        var (rows, _) = await PageAsync(recipeId, limit: 5, position: PositionFrom(versionNumber: 3));

        Assert.Equal([2, 1], rows.Select(row => row.VersionNumber));
    }

    // --- Two workspaces ----------------------------------------------------------------------------------

    /// <summary>
    /// The query filters on recipe id alone — the workspace comes from the global query filter — so this is
    /// the test that says the filter, and not a hand-written predicate, is what holds the boundary.
    /// </summary>
    [Fact]
    public async Task A_recipes_history_is_invisible_from_the_other_workspace()
    {
        var inB = await AddAsync(versions: 3, workspaceId: SqlServerRecipeFixture.WorkspaceB);

        var (fromA, hasMore) = await PageAsync(inB, limit: 25, workspaceId: SqlServerRecipeFixture.WorkspaceA);

        // Named exactly, by a caller who knows the id, and there is nothing there.
        Assert.Empty(fromA);
        Assert.False(hasMore);

        var (fromB, _) = await PageAsync(inB, limit: 25, workspaceId: SqlServerRecipeFixture.WorkspaceB);

        Assert.Equal(3, fromB.Count);
    }

    [Fact]
    public async Task Two_recipes_histories_do_not_bleed_into_each_other()
    {
        var first = await AddAsync(versions: 2);
        var second = await AddAsync(versions: 3);

        var (rowsForFirst, _) = await PageAsync(first, limit: 25);
        var (rowsForSecond, _) = await PageAsync(second, limit: 25);

        Assert.Equal(2, rowsForFirst.Count);
        Assert.Equal(3, rowsForSecond.Count);
        Assert.Empty(rowsForFirst.Select(row => row.Id).Intersect(rowsForSecond.Select(row => row.Id)));
    }

    // --- Helpers -----------------------------------------------------------------------------------------

    private static IRecipeVersionRepository Versions(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IRecipeVersionRepository>();

    private async Task<(IReadOnlyList<RecipeVersionHistoryRecord> Rows, bool HasMore)> PageAsync(
        Guid recipeId,
        int limit,
        RecipeVersionHistoryPosition? position = null,
        Guid? workspaceId = null)
    {
        await using var scope = fixture.ScopeFor(workspaceId ?? SqlServerRecipeFixture.WorkspaceA);

        return await Versions(scope).ListHistoryAsync(
            new RecipeVersionHistoryCriteria(recipeId, TestScope, position, limit),
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Follows every page to the end, guarding against a query that never terminates.
    /// </summary>
    /// <remarks>
    /// The position is rebuilt by encoding the last row into a real cursor and decoding it back, which is the
    /// round trip the read seam actually performs. Building the position directly from the row would skip the
    /// wire format, and a sort value that could not survive it would then only fail in production.
    /// </remarks>
    private async Task<List<RecipeVersionHistoryRecord>> DrainAsync(Guid recipeId, int limit)
    {
        var all = new List<RecipeVersionHistoryRecord>();
        RecipeVersionHistoryPosition? position = null;

        for (var safety = 0; safety < 100; safety++)
        {
            var (rows, hasMore) = await PageAsync(recipeId, limit, position);
            all.AddRange(rows);

            if (!hasMore)
            {
                return all;
            }

            Assert.NotEmpty(rows);

            var last = rows[^1];
            var encoded = ReferenceCursor.Encode(last.SortValue, last.TieBreaker, TestScope);

            Assert.True(ReferenceCursor.TryDecode(encoded, out var cursor));
            Assert.True(RecipeVersionHistoryPosition.TryCreate(cursor!, out position));
        }

        Assert.Fail("paging did not terminate");

        return all;
    }

    private static RecipeVersionHistoryPosition PositionFrom(int versionNumber)
    {
        var encoded = ReferenceCursor.Encode(
            versionNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Guid.NewGuid().ToString("D"),
            TestScope);

        Assert.True(ReferenceCursor.TryDecode(encoded, out var cursor));
        Assert.True(RecipeVersionHistoryPosition.TryCreate(cursor!, out var position));

        return position!;
    }

    /// <summary>
    /// Seeds one recipe and a chain of versions beneath it. <c>WorkspaceId</c> is never set on anything — the
    /// ownership interceptor stamps it from the resolved context, and feature code assigning it is a defect.
    /// </summary>
    /// <param name="withSnapshots">
    /// Gives every version a real stored document, so that "the archive is not read" is a claim about the query
    /// rather than about there being nothing to read.
    /// </param>
    /// <param name="sameInstant">
    /// Writes every version at the same <c>CreatedAt</c>, so an ordering that fell back to the timestamp would
    /// have nothing to order by.
    /// </param>
    /// <param name="proposalOnVersion">
    /// Marks one version as an accepted AI proposal. The pair must be set together —
    /// <c>CK_RecipeVersions_Proposal_Source</c> refuses a proposal id without the matching source, and a source
    /// without the id.
    /// </param>
    private async Task<Guid> AddAsync(
        int versions,
        Guid? workspaceId = null,
        bool withSnapshots = false,
        bool sameInstant = false,
        (int VersionNumber, Guid ProposalId)? proposalOnVersion = null)
    {
        await using var scope = fixture.ScopeFor(workspaceId ?? SqlServerRecipeFixture.WorkspaceA);
        var db = SqlServerRecipeFixture.Db(scope);

        var recipe = new Recipe
        {
            Id = Guid.NewGuid(),
            Title = "Olive oil cake",
            Status = RecipeStatus.Draft,
            CreatedByMembershipId = SqlServerRecipeFixture.AuthorOne,
            UpdatedByMembershipId = SqlServerRecipeFixture.AuthorOne,
            CreatedAt = Now,
            UpdatedAt = Now,
        };

        db.Recipes.Add(recipe);

        Guid? parentId = null;
        for (var number = 1; number <= versions; number++)
        {
            var fromProposal = proposalOnVersion is { } proposal && proposal.VersionNumber == number;

            var version = new RecipeVersion
            {
                Id = Guid.NewGuid(),
                RecipeId = recipe.Id,
                VersionNumber = number,
                Source = fromProposal ? RecipeVersionSource.AiProposalAccepted : RecipeVersionSource.CreatorEdit,
                AiProposalId = fromProposal ? proposalOnVersion!.Value.ProposalId : null,
                Readiness = RecipeVersionReadiness.Draft,
                Reason = number == 1 ? null : $"Edit {number}",
                CreatedByMembershipId = SqlServerRecipeFixture.AuthorOne,
                CreatedAt = sameInstant ? Now : Now.AddMinutes(number),
                ParentVersionId = parentId,
                SnapshotSchemaVersion = RecipeSnapshotDocument.CurrentSchemaVersion,
            };

            if (withSnapshots)
            {
                version.Snapshot = new RecipeVersionSnapshot
                {
                    RecipeVersionId = version.Id,
                    Document = RecipeSnapshotSerializer.Serialize(
                        RecipeSnapshotMapper.Capture(new CompleteRecipe(recipe, null))),
                };
            }

            parentId = version.Id;
            db.RecipeVersions.Add(version);
        }

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return recipe.Id;
    }
}

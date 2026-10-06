using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Modules.Media.Data;
using CreatorPantry.Domain.Modules.Media.Data.Entities;
using CreatorPantry.Domain.Modules.Media.Managers;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Media;

/// <summary>
/// The DAM detail read (DAM-003) against a real SQL Server: what the four statements find, how the histories
/// order, what a tombstone does, and the two workspaces.
/// </summary>
/// <remarks>
/// <para>
/// SQL Server rather than SQLite for the reason <c>MediaAssetSearchSqlServerTests</c> gives: the correlated
/// counts, the tag join and the utilization keyset predicate all have to <em>translate</em>, and a query that
/// compiles in C# and throws passes every test above this layer. <c>RowVersion</c> is the other reason — SQLite
/// has no <c>rowversion</c>, so a concurrency token read is only honest here.
/// </para>
/// <para>
/// Lineage resolution is deliberately absent: naming a recipe or a prompt is two other modules' facades, which
/// <c>MediaAssetDetailEndpointTests</c> exercises through the real host. What belongs here is that the link rows
/// and the counts come back correctly.
/// </para>
/// </remarks>
public sealed class MediaAssetDetailSqlServerTests(SqlServerMediaFixture fixture)
    : IClassFixture<SqlServerMediaFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = SqlServerMediaFixture.Now;

    private const string TestScope = "t";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await using var scope = fixture.ScopeFor(SqlServerMediaFixture.WorkspaceA);

        await SqlServerMediaFixture.Db(scope).Database.ExecuteSqlRawAsync(
            """
            DELETE FROM RecipeAssetLinks;
            DELETE FROM MediaAssetTags;
            DELETE FROM MediaAssetUtilizations;
            DELETE FROM MediaAssetVersions;
            DELETE FROM Recipes;
            DELETE FROM MediaAssets;
            """,
            Ct);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // --- Found ------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_detail_read_carries_the_creators_metadata_and_a_concurrency_token()
    {
        var id = await AddAsync(
            "Soda bread hero",
            description: "Overhead on linen.",
            altText: "A round loaf, slashed across the top.",
            channelKey: "instagram",
            platformKey: "reels",
            day: DayOfWeek.Wednesday,
            styleKey: "overhead-linen",
            cuisineId: SqlServerMediaFixture.CuisineId,
            courseId: SqlServerMediaFixture.CourseId,
            rightsHolder: "Sam Okafor",
            attributionText: "Photo: Sam Okafor");

        var bundle = await FindAsync(id);
        var asset = bundle.Asset;

        Assert.Equal("Soda bread hero", asset.Title);
        Assert.Equal("Overhead on linen.", asset.Description);
        Assert.Equal("A round loaf, slashed across the top.", asset.AltText);
        Assert.Equal("instagram", asset.ChannelKey);
        Assert.Equal("reels", asset.PlatformKey);
        Assert.Equal(DayOfWeek.Wednesday, asset.Day);
        Assert.Equal("overhead-linen", asset.StyleKey);
        Assert.Equal(SqlServerMediaFixture.CuisineId, asset.CuisineId);
        Assert.Equal(SqlServerMediaFixture.CourseId, asset.CourseId);
        Assert.Equal("Sam Okafor", asset.RightsHolder);
        Assert.Equal("Photo: Sam Okafor", asset.AttributionText);
        Assert.Null(asset.DeletedAt);

        // A real rowversion, which is the other reason these tests need SQL Server: SQLite has none, so a token
        // read there would only ever prove that the column the test itself set came back.
        Assert.Equal(MediaConcurrencyToken.ByteLength, asset.RowVersion.Length);
        Assert.NotEqual(new byte[MediaConcurrencyToken.ByteLength], asset.RowVersion);
    }

    /// <summary>
    /// Null for an unknown id, so the layer above answers 404 without ever learning whether the asset exists
    /// somewhere else.
    /// </summary>
    [Fact]
    public async Task An_unknown_id_is_null()
    {
        Assert.Null(await Detail().FindAsync(Guid.NewGuid(), includeDeleted: false, Ct));
    }

    /// <summary>
    /// The three counts come from the same statement as the root. Each is given a different value so a
    /// transposition between them cannot pass.
    /// </summary>
    [Fact]
    public async Task The_counts_report_versions_uses_and_recipe_links_separately()
    {
        var recipeOne = await AddRecipeAsync("Soda bread");
        var recipeTwo = await AddRecipeAsync("Olive oil cake");
        var id = await AddAsync("Soda bread hero");

        await AddVersionsAsync(id, 2, 3);
        await AddUsesAsync(id, 5);
        await LinkAsync(id, recipeOne, 0);
        await LinkAsync(id, recipeTwo, 1);

        var asset = (await FindAsync(id)).Asset;

        Assert.Equal(3, asset.VersionCount);
        Assert.Equal(5, asset.UtilizationCount);
        Assert.Equal(2, asset.RecipeLinkCount);
    }

    /// <summary>
    /// Another asset's versions, uses and links do not inflate this one's counts — the correlated subqueries are
    /// the part of that statement a mistake would be invisible in.
    /// </summary>
    [Fact]
    public async Task The_counts_ignore_another_assets_children()
    {
        var recipe = await AddRecipeAsync("Soda bread");
        var mine = await AddAsync("Mine");
        var other = await AddAsync("Other");

        await AddVersionsAsync(other, 2, 4);
        await AddUsesAsync(other, 7);
        await LinkAsync(other, recipe, 0);

        var asset = (await FindAsync(mine)).Asset;

        Assert.Equal(1, asset.VersionCount);
        Assert.Equal(0, asset.UtilizationCount);
        Assert.Equal(0, asset.RecipeLinkCount);
    }

    [Fact]
    public async Task Versions_come_back_newest_first_with_their_media_facts_and_no_object_key()
    {
        var id = await AddAsync("Soda bread hero");
        await AddVersionsAsync(id, 2, 3);

        var versions = (await FindAsync(id)).Versions;

        Assert.Equal([3, 2, 1], versions.Select(version => version.VersionNumber));
        Assert.All(versions, version =>
        {
            Assert.Equal("image/jpeg", version.MediaType);
            Assert.True(version.Width > 0);
            Assert.True(version.Height > 0);
            Assert.StartsWith("sha256:", version.ContentChecksum);
        });

        // The projection selects nine columns and ObjectKey is not among them, which is stronger than reading the
        // entity and dropping it: a later edit cannot reintroduce it by accident (media.md).
        Assert.DoesNotContain(
            typeof(MediaAssetVersionRecord).GetProperties(),
            property => property.Name.Contains("ObjectKey", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Tags_come_back_named_from_the_workspaces_own_vocabulary()
    {
        var id = await AddAsync(
            "Soda bread hero",
            tagIds: [SqlServerMediaFixture.SecondTagIdA, SqlServerMediaFixture.TagIdA]);

        var tags = (await FindAsync(id)).Tags;

        // Alphabetical, so a panel renders them in a stable order rather than in insertion order.
        Assert.Equal(["Freezer", "Weeknight"], tags.Select(tag => tag.Name));
        Assert.Contains(SqlServerMediaFixture.TagIdA, tags.Select(tag => tag.Id));
    }

    [Fact]
    public async Task Recipe_links_come_back_in_their_own_order_with_role_and_caption()
    {
        var hero = await AddRecipeAsync("Soda bread");
        var gallery = await AddRecipeAsync("Olive oil cake");
        var id = await AddAsync("Shared photograph");

        await LinkAsync(id, gallery, 1, RecipeAssetRole.Gallery, "On the cooling rack");
        await LinkAsync(id, hero, 0, RecipeAssetRole.Hero, "The hero shot");

        var links = (await FindAsync(id)).RecipeLinks;

        Assert.Equal([hero, gallery], links.Select(link => link.RecipeId));
        Assert.Equal([RecipeAssetRole.Hero, RecipeAssetRole.Gallery], links.Select(link => link.Role));
        Assert.Equal(["The hero shot", "On the cooling rack"], links.Select(link => link.Caption));
    }

    /// <summary>
    /// An asset with none of anything comes back with empty lists rather than nulls, so a caller never has to
    /// distinguish "no versions" from "not read".
    /// </summary>
    [Fact]
    public async Task An_asset_with_no_children_comes_back_with_empty_lists()
    {
        var id = await AddAsync("Bare", withVersion: false);
        var bundle = await FindAsync(id);

        Assert.Empty(bundle.Versions);
        Assert.Empty(bundle.Tags);
        Assert.Empty(bundle.RecipeLinks);
        Assert.Equal(0, bundle.Asset.VersionCount);
    }

    // --- Soft delete ------------------------------------------------------------------------------------

    /// <summary>
    /// The approved policy: an ordinary read of a tombstone finds nothing, and the same read with
    /// <c>includeDeleted</c> finds it with its tombstone intact. One predicate, two answers.
    /// </summary>
    [Fact]
    public async Task A_soft_deleted_asset_is_found_only_when_asked_for()
    {
        var actor = Guid.NewGuid();
        var id = await AddAsync("Deleted hero", deletedAt: Now, deletedBy: actor);

        Assert.Null(await Detail().FindAsync(id, includeDeleted: false, Ct));

        var asset = (await FindAsync(id, includeDeleted: true)).Asset;

        Assert.Equal(Now, asset.DeletedAt);
        Assert.Equal(actor, asset.DeletedByMembershipId);
    }

    /// <summary>
    /// Asking for deleted assets does not hide live ones: the predicate widens the set rather than switching it.
    /// </summary>
    [Fact]
    public async Task Asking_for_deleted_assets_still_finds_a_live_one()
    {
        var id = await AddAsync("Live");

        Assert.Null((await FindAsync(id, includeDeleted: true)).Asset.DeletedAt);
    }

    /// <summary>
    /// A tombstone keeps its versions, tags, links and counts. 12.9e deliberately leaves links standing, so a
    /// creator can see what a deletion would affect rather than finding it silently cut.
    /// </summary>
    [Fact]
    public async Task A_tombstone_still_reports_its_children()
    {
        var recipe = await AddRecipeAsync("Soda bread");
        var id = await AddAsync("Deleted hero", tagIds: [SqlServerMediaFixture.TagIdA], deletedAt: Now);

        await AddUsesAsync(id, 2);
        await LinkAsync(id, recipe, 0);

        var bundle = await FindAsync(id, includeDeleted: true);

        Assert.Single(bundle.Versions);
        Assert.Single(bundle.Tags);
        Assert.Single(bundle.RecipeLinks);
        Assert.Equal(2, bundle.Asset.UtilizationCount);
    }

    // --- Utilization history ----------------------------------------------------------------------------

    [Fact]
    public async Task A_utilization_history_comes_back_newest_first_with_its_total()
    {
        var id = await AddAsync("Soda bread hero");

        await AddUseAsync(id, new DateOnly(2026, 3, 1), "instagram");
        await AddUseAsync(id, new DateOnly(2026, 5, 1), "blog");
        await AddUseAsync(id, new DateOnly(2026, 4, 1), "newsletter");

        var (rows, hasMore, total) = await PageAsync(id, limit: 10);

        Assert.Equal(["blog", "newsletter", "instagram"], rows.Select(use => use.PlatformKey));
        Assert.False(hasMore);
        Assert.Equal(3, total);
    }

    /// <summary>
    /// The day is read back as it was written, not re-derived: deriving it here would use this server's calendar
    /// rather than the workspace's zone, which is the whole reason the column exists (DAM-009).
    /// </summary>
    [Fact]
    public async Task A_use_reports_the_day_that_was_logged_with_it()
    {
        var id = await AddAsync("Soda bread hero");
        await AddUseAsync(id, new DateOnly(2026, 4, 1), "instagram", day: DayOfWeek.Saturday);

        var (rows, _, _) = await PageAsync(id, limit: 10);

        Assert.Equal(DayOfWeek.Saturday, Assert.Single(rows).UtilizedDay);
    }

    [Fact]
    public async Task An_asset_never_used_has_an_empty_history_rather_than_an_error()
    {
        var id = await AddAsync("Never used");

        var (rows, hasMore, total) = await PageAsync(id, limit: 10);

        Assert.Empty(rows);
        Assert.False(hasMore);
        Assert.Equal(0, total);
    }

    /// <summary>
    /// Thirteen uses read three at a time: every row exactly once, in the same order the unpaged read gives.
    /// This is the test a keyset predicate that disagrees with its <c>ORDER BY</c> fails.
    /// </summary>
    [Fact]
    public async Task Paging_a_history_visits_every_use_exactly_once()
    {
        var id = await AddAsync("Soda bread hero");

        for (var index = 0; index < 13; index++)
        {
            await AddUseAsync(id, new DateOnly(2026, 1, 1).AddDays(index), $"platform-{index:D2}");
        }

        var drained = await DrainAsync(id, limit: 3);
        var (unpaged, _, _) = await PageAsync(id, limit: 100);

        Assert.Equal(13, drained.Count);
        Assert.Equal(unpaged.Select(use => use.Id), drained.Select(use => use.Id));
        Assert.Equal(13, drained.Select(use => use.Id).Distinct().Count());
    }

    /// <summary>
    /// Nine uses on one day, so the only thing separating them is the id in the tie-break. Without it in both the
    /// <c>WHERE</c> and the <c>ORDER BY</c> a page boundary inside the day repeats rows or skips them — and an
    /// asset used several times on one day is the ordinary case here, not a contrived one.
    /// </summary>
    [Fact]
    public async Task Paging_through_uses_sharing_one_day_neither_repeats_nor_skips()
    {
        var id = await AddAsync("Soda bread hero");
        var day = new DateOnly(2026, 4, 1);

        for (var index = 0; index < 9; index++)
        {
            await AddUseAsync(id, day, $"platform-{index}");
        }

        var drained = await DrainAsync(id, limit: 2);

        Assert.Equal(9, drained.Count);
        Assert.Equal(9, drained.Select(use => use.Id).Distinct().Count());
    }

    [Fact]
    public async Task A_history_never_includes_another_assets_uses()
    {
        var mine = await AddAsync("Mine");
        var other = await AddAsync("Other");

        await AddUseAsync(mine, new DateOnly(2026, 4, 1), "mine");
        await AddUseAsync(other, new DateOnly(2026, 4, 2), "theirs");

        var (rows, _, total) = await PageAsync(mine, limit: 10);

        Assert.Equal("mine", Assert.Single(rows).PlatformKey);
        Assert.Equal(1, total);
    }

    [Fact]
    public async Task A_caller_that_did_not_ask_for_a_total_is_not_given_one()
    {
        var id = await AddAsync("Soda bread hero");
        await AddUsesAsync(id, 2);

        var (_, _, total) = await PageAsync(id, limit: 10, includeTotal: false);

        Assert.Null(total);
    }

    // --- Isolation --------------------------------------------------------------------------------------

    /// <summary>
    /// Workspace A and Workspace B, each holding an asset of the same shape — the two-workspace coverage
    /// tenancy.md requires. A's read of B's id finds nothing, which is what lets the layer above answer 404
    /// without disclosing that it exists.
    /// </summary>
    [Fact]
    public async Task An_asset_of_the_other_workspace_is_invisible_rather_than_forbidden()
    {
        var inB = await AddAsync("B's hero", workspaceId: SqlServerMediaFixture.WorkspaceB);

        Assert.Null(await Detail().FindAsync(inB, includeDeleted: false, Ct));

        // And not reachable by asking for tombstones either, which would be the obvious way to widen the read.
        Assert.Null(await Detail().FindAsync(inB, includeDeleted: true, Ct));

        await using var scope = fixture.ScopeFor(SqlServerMediaFixture.WorkspaceB);
        Assert.NotNull(await DetailIn(scope).FindAsync(inB, includeDeleted: false, Ct));
    }

    /// <summary>
    /// The counts are correlated subqueries, which is exactly where a missing filter would leak a number while
    /// returning none of the rows behind it. Both workspaces hold children of the same shape.
    /// </summary>
    [Fact]
    public async Task The_counts_never_count_the_other_workspaces_children()
    {
        var recipeInB = await AddRecipeAsync("B's recipe", SqlServerMediaFixture.WorkspaceB);
        var inB = await AddAsync("B's hero", workspaceId: SqlServerMediaFixture.WorkspaceB);

        await AddVersionsAsync(inB, 2, 3, SqlServerMediaFixture.WorkspaceB);
        await AddUsesAsync(inB, 4, SqlServerMediaFixture.WorkspaceB);
        await LinkAsync(inB, recipeInB, 0, workspaceId: SqlServerMediaFixture.WorkspaceB);

        var inA = await AddAsync("A's hero");
        var asset = (await FindAsync(inA)).Asset;

        Assert.Equal(1, asset.VersionCount);
        Assert.Equal(0, asset.UtilizationCount);
        Assert.Equal(0, asset.RecipeLinkCount);
    }

    /// <summary>
    /// An asset cannot carry the other workspace's tag at all, which is what makes the join safe.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This replaced a test that proved nothing.</strong> The earlier version read B's asset from B's own
    /// scope and asserted the tag id came back as B's — but the id comes from the <c>MediaAssetTag</c> row itself
    /// and <c>WorkspaceTag</c> is keyed on <c>Id</c> alone, so that assertion held whether or not the join was
    /// workspace-aware. It would have passed against a join with no filtering.
    /// </para>
    /// <para>
    /// The real guarantee is one level down: <c>MediaAssetTag</c> carries <c>(WorkspaceId, WorkspaceTagId)</c> to
    /// <c>(WorkspaceId, Id)</c> on <c>WorkspaceTag</c>, so a row naming a neighbour's tag is unrepresentable. Both
    /// workspaces have a tag called "Weeknight" with different ids, so this is refused on ownership rather than on
    /// the name.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task An_asset_cannot_carry_the_other_workspaces_tag()
    {
        await using var scope = fixture.ScopeFor(SqlServerMediaFixture.WorkspaceA);
        var db = SqlServerMediaFixture.Db(scope);

        var asset = SeededMediaAsset.For(SqlServerMediaFixture.WorkspaceA, at: Now);
        asset.WorkspaceId = Guid.Empty;
        asset.Tags.Add(new MediaAssetTag
        {
            MediaAssetId = asset.Id,
            WorkspaceTagId = SqlServerMediaFixture.TagIdB,
        });

        db.MediaAssets.Add(asset);

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
    }

    /// <summary>
    /// B's asset id read from A's scope finds no history, which pins the filter on
    /// <c>MediaAssetUtilizations</c> itself rather than relying on the visibility check above it.
    /// </summary>
    [Fact]
    public async Task A_history_read_from_the_other_workspace_finds_nothing()
    {
        var inB = await AddAsync("B's hero", workspaceId: SqlServerMediaFixture.WorkspaceB);
        await AddUseAsync(inB, new DateOnly(2026, 4, 1), "theirs", workspaceId: SqlServerMediaFixture.WorkspaceB);

        var (fromA, hasMore, totalFromA) = await PageAsync(inB, limit: 100);

        Assert.Empty(fromA);
        Assert.False(hasMore);
        Assert.Equal(0, totalFromA);

        // And B sees its own, so the empty page above is the filter working rather than a seed that never landed.
        var (fromB, _, totalFromB) = await PageAsync(
            inB, limit: 100, workspaceId: SqlServerMediaFixture.WorkspaceB);

        Assert.Equal("theirs", Assert.Single(fromB).PlatformKey);
        Assert.Equal(1, totalFromB);
    }

    /// <summary>
    /// A position minted against one asset's history, replayed against another's, pages that other asset's rows
    /// and never reaches across. The scope fingerprint is what turns this into a refused cursor above; this is
    /// the evidence the repository does not leak even when that check has been bypassed entirely.
    /// </summary>
    [Fact]
    public async Task A_position_from_one_assets_history_pages_only_the_other_assets_rows()
    {
        var mine = await AddAsync("Mine");
        var other = await AddAsync("Other");

        // Mine's uses are strictly later than other's, so a position from mine's newest row is later than
        // everything in other's history and the expected page is all of it.
        await AddUseAsync(mine, new DateOnly(2026, 6, 1), "mine-one");
        await AddUseAsync(mine, new DateOnly(2026, 5, 1), "mine-two");
        await AddUseAsync(other, new DateOnly(2026, 2, 1), "other-one");
        await AddUseAsync(other, new DateOnly(2026, 1, 1), "other-two");

        var (first, _, _) = await PageAsync(mine, limit: 1);
        var position = PositionFrom(first[0]);

        var (rows, _, _) = await PageAsync(other, limit: 100, position: position);

        Assert.Equal(["other-one", "other-two"], rows.Select(use => use.PlatformKey));
    }

    // --- Helpers ----------------------------------------------------------------------------------------

    private static IMediaAssetDetailRepository DetailIn(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IMediaAssetDetailRepository>();

    private IMediaAssetDetailRepository Detail()
    {
        var scope = fixture.ScopeFor(SqlServerMediaFixture.WorkspaceA);

        return DetailIn(scope);
    }

    private async Task<MediaAssetDetailBundle> FindAsync(Guid id, bool includeDeleted = false)
    {
        await using var scope = fixture.ScopeFor(SqlServerMediaFixture.WorkspaceA);
        var bundle = await DetailIn(scope).FindAsync(id, includeDeleted, Ct);

        return Assert.IsType<MediaAssetDetailBundle>(bundle);
    }

    private async Task<(IReadOnlyList<MediaAssetUtilizationRecord> Rows, bool HasMore, int? Total)> PageAsync(
        Guid mediaAssetId,
        int limit,
        MediaAssetUtilizationPosition? position = null,
        bool includeTotal = true,
        Guid? workspaceId = null)
    {
        await using var scope = fixture.ScopeFor(workspaceId ?? SqlServerMediaFixture.WorkspaceA);

        return await DetailIn(scope).ListUtilizationAsync(
            new MediaAssetUtilizationCriteria(mediaAssetId, TestScope, position, limit, includeTotal), Ct);
    }

    /// <inheritdoc cref="MediaAssetSearchSqlServerTests.DrainAsync"/>
    private async Task<List<MediaAssetUtilizationRecord>> DrainAsync(Guid mediaAssetId, int limit)
    {
        var all = new List<MediaAssetUtilizationRecord>();
        MediaAssetUtilizationPosition? position = null;

        for (var safety = 0; safety < 100; safety++)
        {
            var (rows, hasMore, _) = await PageAsync(mediaAssetId, limit, position);
            all.AddRange(rows);

            if (!hasMore)
            {
                return all;
            }

            Assert.Equal(limit, rows.Count);
            position = PositionFrom(rows[^1]);
        }

        Assert.Fail("paging did not terminate");

        return all;
    }

    /// <summary>
    /// Rebuilds the position by encoding the row into a real cursor and decoding it back, which is the round trip
    /// the facade performs. Building it straight from the row would skip the wire format, and a sort value that
    /// could not survive it would then only fail in production.
    /// </summary>
    private static MediaAssetUtilizationPosition PositionFrom(MediaAssetUtilizationRecord row)
    {
        var encoded = ReferenceCursor.Encode(row.SortValue, row.TieBreaker, TestScope);

        Assert.True(ReferenceCursor.TryDecode(encoded, out var cursor));
        Assert.True(MediaAssetUtilizationPosition.TryCreate(cursor!, out var position));

        return position!;
    }

    /// <summary>
    /// Seeds one asset and only the parts a test asked for. <c>WorkspaceId</c> is never set — the ownership
    /// interceptor stamps it from the resolved context, and feature code assigning it is a defect.
    /// </summary>
    private async Task<Guid> AddAsync(
        string title,
        Guid? workspaceId = null,
        string? description = null,
        string? altText = null,
        string? channelKey = null,
        string? platformKey = null,
        DayOfWeek? day = null,
        string? styleKey = null,
        Guid? cuisineId = null,
        Guid? courseId = null,
        string? rightsHolder = null,
        string? attributionText = null,
        IReadOnlyList<Guid>? tagIds = null,
        DateTimeOffset? deletedAt = null,
        Guid? deletedBy = null,
        bool withVersion = true)
    {
        await using var scope = fixture.ScopeFor(workspaceId ?? SqlServerMediaFixture.WorkspaceA);
        var db = SqlServerMediaFixture.Db(scope);
        var actor = Guid.NewGuid();

        var asset = new MediaAsset
        {
            Id = Guid.NewGuid(),
            Title = title,
            Description = description,
            AltText = altText,
            Kind = MediaAssetKind.Original,
            ChannelKey = channelKey,
            PlatformKey = platformKey,
            Day = day,
            StyleKey = styleKey,
            CuisineId = cuisineId,
            CourseId = courseId,
            RightsHolder = rightsHolder,
            AttributionText = attributionText,
            CurrentVersionNumber = 1,
            DeletedAt = deletedAt,
            DeletedByMembershipId = deletedAt is null ? null : deletedBy ?? actor,
            CreatedAt = Now,
            UpdatedAt = Now,
            CreatedByMembershipId = actor,
            UpdatedByMembershipId = actor,
        };

        if (withVersion)
        {
            asset.Versions.Add(NewVersion(asset.Id, 1));
        }

        foreach (var tagId in tagIds ?? [])
        {
            asset.Tags.Add(new MediaAssetTag { MediaAssetId = asset.Id, WorkspaceTagId = tagId });
        }

        db.MediaAssets.Add(asset);
        await db.SaveChangesAsync(Ct);

        return asset.Id;
    }

    private static MediaAssetVersion NewVersion(Guid mediaAssetId, int versionNumber) => new()
    {
        Id = Guid.NewGuid(),
        MediaAssetId = mediaAssetId,
        VersionNumber = versionNumber,
        MediaType = "image/jpeg",
        SizeBytes = 204_800 + versionNumber,
        Width = 1600,
        Height = 1200,
        ContentChecksum = $"sha256:{Convert.ToHexString(Guid.NewGuid().ToByteArray())}",
        ObjectKey = $"assets/{mediaAssetId:D}/{versionNumber}.jpg",
        OriginalFileName = $"soda-bread-{versionNumber}.jpg",
        Source = MediaAssetVersionSource.Upload,
        CreatedByMembershipId = Guid.NewGuid(),
        CreatedAt = Now.AddMinutes(versionNumber),
    };

    /// <summary>Adds versions <paramref name="from"/> through <paramref name="to"/> inclusive.</summary>
    private async Task AddVersionsAsync(Guid mediaAssetId, int from, int to, Guid? workspaceId = null)
    {
        await using var scope = fixture.ScopeFor(workspaceId ?? SqlServerMediaFixture.WorkspaceA);
        var db = SqlServerMediaFixture.Db(scope);

        for (var versionNumber = from; versionNumber <= to; versionNumber++)
        {
            db.MediaAssetVersions.Add(NewVersion(mediaAssetId, versionNumber));
        }

        await db.SaveChangesAsync(Ct);
    }

    private async Task AddUsesAsync(Guid mediaAssetId, int count, Guid? workspaceId = null)
    {
        for (var index = 0; index < count; index++)
        {
            await AddUseAsync(
                mediaAssetId, new DateOnly(2026, 1, 1).AddDays(index), $"platform-{index}",
                workspaceId: workspaceId);
        }
    }

    private async Task AddUseAsync(
        Guid mediaAssetId,
        DateOnly utilizedOn,
        string platformKey,
        DayOfWeek? day = null,
        Guid? workspaceId = null)
    {
        await using var scope = fixture.ScopeFor(workspaceId ?? SqlServerMediaFixture.WorkspaceA);
        var db = SqlServerMediaFixture.Db(scope);

        db.MediaAssetUtilizations.Add(new MediaAssetUtilization
        {
            Id = Guid.NewGuid(),
            MediaAssetId = mediaAssetId,
            PlatformKey = platformKey,

            // The day is stored as logged rather than derived here, which is the behaviour under test: a use is
            // logged against a calendar day in the workspace's zone (DAM-009).
            UtilizedOn = utilizedOn,
            UtilizedDay = day ?? utilizedOn.DayOfWeek,
            LoggedByMembershipId = Guid.NewGuid(),
            CreatedAt = Now,
        });

        await db.SaveChangesAsync(Ct);
    }

    private async Task LinkAsync(
        Guid mediaAssetId,
        Guid recipeId,
        int sortOrder,
        RecipeAssetRole role = RecipeAssetRole.Gallery,
        string? caption = null,
        Guid? workspaceId = null)
    {
        await using var scope = fixture.ScopeFor(workspaceId ?? SqlServerMediaFixture.WorkspaceA);
        var db = SqlServerMediaFixture.Db(scope);

        db.RecipeAssetLinks.Add(new RecipeAssetLink
        {
            Id = Guid.NewGuid(),
            RecipeId = recipeId,
            MediaAssetId = mediaAssetId,
            Role = role,
            Caption = caption,
            SortOrder = sortOrder,
        });

        await db.SaveChangesAsync(Ct);
    }

    private async Task<Guid> AddRecipeAsync(string title, Guid? workspaceId = null)
    {
        await using var scope = fixture.ScopeFor(workspaceId ?? SqlServerMediaFixture.WorkspaceA);
        var db = SqlServerMediaFixture.Db(scope);
        var actor = Guid.NewGuid();

        var recipe = new Recipe
        {
            Id = Guid.NewGuid(),
            Title = title,
            Status = RecipeStatus.Draft,
            CreatedByMembershipId = actor,
            UpdatedByMembershipId = actor,
            CreatedAt = Now,
            UpdatedAt = Now,
        };

        db.Recipes.Add(recipe);
        await db.SaveChangesAsync(Ct);

        return recipe.Id;
    }
}

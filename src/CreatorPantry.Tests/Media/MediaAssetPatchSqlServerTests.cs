using CreatorPantry.Domain.Managers.Patching;
using CreatorPantry.Domain.Modules.Media.Business;
using CreatorPantry.Domain.Modules.Media.Data;
using CreatorPantry.Domain.Modules.Media.Data.Entities;
using CreatorPantry.Domain.Modules.Media.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Media;

/// <summary>
/// The metadata patch's optimistic concurrency (DAM-004) against a real SQL Server.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This class exists because SQLite cannot answer the question.</strong>
/// <c>SqliteModelCustomizer</c> gives every concurrency token a <c>randomblob(8)</c> default so inserts work, but
/// nothing bumps it on update — so under SQLite a token always looks unchanged, a stale one always matches, and
/// every assertion about either passes for the wrong reason. <c>rowversion</c> is a database behaviour, and this
/// is the engine that has it.
/// </para>
/// <para>
/// At the DataLayer rather than through the route: the token is the only thing under test, and the HTTP contract
/// around it — which status, which code, which field — is proved by
/// <see cref="MediaAssetPatchEndpointTests"/> where it can be.
/// </para>
/// </remarks>
public sealed class MediaAssetPatchSqlServerTests(SqlServerMediaFixture fixture)
    : IClassFixture<SqlServerMediaFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = SqlServerMediaFixture.Now;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await using var scope = fixture.ScopeFor(SqlServerMediaFixture.WorkspaceA);

        await SqlServerMediaFixture.Db(scope).Database.ExecuteSqlRawAsync(
            """
            DELETE FROM MediaAssetTags;
            DELETE FROM MediaAssetVersions;
            DELETE FROM MediaAssets;
            """,
            Ct);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>
    /// A write moves the token, which is the premise everything else here rests on.
    /// </summary>
    [Fact]
    public async Task A_write_refreshes_the_concurrency_token()
    {
        var id = await AddAsync("Soda bread hero");
        var before = await TokenAsync(id);

        await using var scope = fixture.ScopeFor(SqlServerMediaFixture.WorkspaceA);
        var assets = DataLayer(scope);
        var asset = await assets.FindLiveForUpdateAsync(id, Ct);

        Assert.True(await assets.UpdateMetadataAsync(
            asset!, Merged(asset!, title: "Renamed"), Guid.NewGuid(), Ct));

        Assert.NotEqual(before, MediaConcurrencyToken.From(asset!.RowVersion));
        Assert.NotEqual(before, await TokenAsync(id));
    }

    /// <summary>
    /// The token the caller quoted is checked against the row as it stands, so an edit composed against an older
    /// read is refused. This is the ordinary two-editor case and the reason the token is published at all.
    /// </summary>
    [Fact]
    public async Task A_token_from_an_earlier_read_no_longer_matches()
    {
        var id = await AddAsync("Soda bread hero");
        var stale = await TokenAsync(id);

        // Somebody else's edit lands first.
        await using (var first = fixture.ScopeFor(SqlServerMediaFixture.WorkspaceA))
        {
            var assets = DataLayer(first);
            var asset = await assets.FindLiveForUpdateAsync(id, Ct);
            await assets.UpdateMetadataAsync(asset!, Merged(asset!, title: "First wins"), Guid.NewGuid(), Ct);
        }

        await using var second = fixture.ScopeFor(SqlServerMediaFixture.WorkspaceA);
        var reloaded = await DataLayer(second).FindLiveForUpdateAsync(id, Ct);

        // What Business checks before doing any work, and what answers 409.
        Assert.False(MediaConcurrencyToken.Matches(stale, reloaded!.RowVersion));
        Assert.True(MediaConcurrencyToken.IsWellFormed(stale));

        // And the first edit stands: a refusal is not a rollback of somebody else's work.
        Assert.Equal("First wins", reloaded.Title);
    }

    /// <summary>
    /// The narrow race the token check above cannot cover: two transactions that both read before either wrote.
    /// Business has already compared tokens and both passed, so the only thing left to refuse the second write is
    /// the <c>WHERE RowVersion = @original</c> EF puts in the <c>UPDATE</c> — which is why
    /// <c>UpdateMetadataAsync</c> returns false rather than throwing.
    /// </summary>
    [Fact]
    public async Task Two_transactions_that_both_read_first_cannot_both_write()
    {
        var id = await AddAsync("Soda bread hero");

        await using var left = fixture.ScopeFor(SqlServerMediaFixture.WorkspaceA);
        await using var right = fixture.ScopeFor(SqlServerMediaFixture.WorkspaceA);

        var leftAssets = DataLayer(left);
        var rightAssets = DataLayer(right);

        // Both read the same version of the row.
        var leftAsset = await leftAssets.FindLiveForUpdateAsync(id, Ct);
        var rightAsset = await rightAssets.FindLiveForUpdateAsync(id, Ct);

        Assert.Equal(
            MediaConcurrencyToken.From(leftAsset!.RowVersion),
            MediaConcurrencyToken.From(rightAsset!.RowVersion));

        Assert.True(await leftAssets.UpdateMetadataAsync(
            leftAsset, Merged(leftAsset, title: "Left"), Guid.NewGuid(), Ct));

        // The second write finds the row moved and is refused rather than overwriting the first.
        Assert.False(await rightAssets.UpdateMetadataAsync(
            rightAsset, Merged(rightAsset, title: "Right"), Guid.NewGuid(), Ct));

        Assert.Equal("Left", (await ReloadAsync(id)).Title);
    }

    /// <summary>
    /// A patch that would change nothing is never handed to the DataLayer, so the token stays spendable. Asserted
    /// here rather than only over SQLite because an unchanged token is only evidence on an engine that would have
    /// changed it.
    /// </summary>
    [Fact]
    public async Task A_patch_that_changes_nothing_leaves_the_token_valid()
    {
        var id = await AddAsync("Soda bread hero", description: "Overhead on linen.");
        var token = await TokenAsync(id);

        await using var scope = fixture.ScopeFor(SqlServerMediaFixture.WorkspaceA);
        var asset = await DataLayer(scope).FindLiveForUpdateAsync(id, Ct);

        // What Business asks before writing anything.
        var merged = MediaAssetMetadataMerge.Apply(asset!, new MediaAssetMetadataPatchViewModel
        {
            Title = PatchField<string?>.Submitted("Soda bread hero"),
            Description = PatchField<string?>.Submitted("Overhead on linen."),
        });

        Assert.False(MediaAssetMetadataMerge.Changes(asset!, merged));
        Assert.Equal(token, await TokenAsync(id));
        Assert.True(MediaConcurrencyToken.Matches(token, asset!.RowVersion));
    }

    /// <summary>
    /// The tag replace is a set difference against the real database: a tag that was already there keeps its row
    /// rather than being deleted and reinserted, one that is gone from the request is removed, and a new one is
    /// added.
    /// </summary>
    [Fact]
    public async Task A_tag_replace_adds_and_removes_only_what_changed()
    {
        var id = await AddAsync(
            "Soda bread hero",
            tagIds: [SqlServerMediaFixture.TagIdA, SqlServerMediaFixture.SecondTagIdA]);

        await using var scope = fixture.ScopeFor(SqlServerMediaFixture.WorkspaceA);
        var assets = DataLayer(scope);
        var asset = await assets.FindLiveForUpdateAsync(id, Ct);

        // Keep the second, drop the first. Nothing is added, so the row for the kept tag must survive untouched.
        var merged = MediaAssetMetadataMerge.Apply(asset!, new MediaAssetMetadataPatchViewModel
        {
            Tags = PatchField<IReadOnlyList<Guid>?>.Submitted([SqlServerMediaFixture.SecondTagIdA]),
        });

        Assert.True(await assets.UpdateMetadataAsync(asset!, merged, Guid.NewGuid(), Ct));

        var tags = await TagsAsync(id);

        Assert.Equal([SqlServerMediaFixture.SecondTagIdA], tags);
    }

    /// <summary>
    /// A patch cannot reach the other workspace's asset even at this layer, where no route or policy stands in
    /// front of it: the global query filter is what makes the read null, and null is the 404 above.
    /// </summary>
    [Fact]
    public async Task The_other_workspaces_asset_cannot_be_loaded_for_update()
    {
        var inB = await AddAsync("B's hero", workspaceId: SqlServerMediaFixture.WorkspaceB);

        await using var fromA = fixture.ScopeFor(SqlServerMediaFixture.WorkspaceA);
        Assert.Null(await DataLayer(fromA).FindLiveForUpdateAsync(inB, Ct));

        // B can, so the null above is the filter working rather than a seed that never landed.
        await using var fromB = fixture.ScopeFor(SqlServerMediaFixture.WorkspaceB);
        Assert.NotNull(await DataLayer(fromB).FindLiveForUpdateAsync(inB, Ct));
    }

    /// <summary>A tombstone is excluded by the read itself, so a patch cannot edit or restore one.</summary>
    [Fact]
    public async Task A_soft_deleted_asset_cannot_be_loaded_for_update()
    {
        var id = await AddAsync("Deleted hero", deletedAt: Now);

        await using var scope = fixture.ScopeFor(SqlServerMediaFixture.WorkspaceA);

        Assert.Null(await DataLayer(scope).FindLiveForUpdateAsync(id, Ct));
    }

    /// <summary>
    /// Business refuses a stale token before it writes, which is the check the database's own guard cannot stand
    /// in for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This test exists because removing that check broke nothing.</strong> The two-transaction test above
    /// covers the race where both readers load the same row — there EF's <c>WHERE RowVersion = @original</c>
    /// refuses the second write. But a caller quoting an <em>older</em> read, with nobody else writing since, is a
    /// different case: Business loads the row fresh, so EF's guard compares the current value against itself and
    /// succeeds. Without the token comparison that edit silently overwrites whatever happened in between.
    /// </para>
    /// <para>
    /// Driven through <c>IMediaAssetBusiness</c> rather than the DataLayer for exactly that reason — the check
    /// being tested lives there — and on SQL Server because a stale token is unreachable on an engine that never
    /// moves one.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Business_refuses_an_edit_quoting_a_superseded_token()
    {
        var id = await AddAsync("Soda bread hero");
        var stale = await TokenAsync(id);

        // An earlier edit moves the row on, and the caller below has not seen it.
        await using (var earlier = fixture.ScopeFor(SqlServerMediaFixture.WorkspaceA))
        {
            var assets = DataLayer(earlier);
            var asset = await assets.FindLiveForUpdateAsync(id, Ct);
            await assets.UpdateMetadataAsync(asset!, Merged(asset!, title: "Somebody else"), Guid.NewGuid(), Ct);
        }

        await using var scope = fixture.ScopeFor(SqlServerMediaFixture.WorkspaceA);
        var result = await Business(scope).PatchMetadataAsync(
            id,
            new MediaAssetMetadataPatchViewModel
            {
                ExpectedConcurrencyToken = stale,
                Title = PatchField<string?>.Submitted("Overwritten"),
            },
            Guid.NewGuid(),
            Ct);

        Assert.False(result.Succeeded);
        Assert.Equal(MediaErrorCodes.AssetStaleToken, result.Error!.Code);

        // The earlier edit stands, untouched.
        Assert.Equal("Somebody else", (await ReloadAsync(id)).Title);
    }

    /// <summary>
    /// The same call with the current token succeeds, so the refusal above is the token check and not a patch path
    /// that cannot write at all.
    /// </summary>
    [Fact]
    public async Task Business_accepts_an_edit_quoting_the_current_token()
    {
        var id = await AddAsync("Soda bread hero");

        await using var scope = fixture.ScopeFor(SqlServerMediaFixture.WorkspaceA);
        var result = await Business(scope).PatchMetadataAsync(
            id,
            new MediaAssetMetadataPatchViewModel
            {
                ExpectedConcurrencyToken = await TokenAsync(id),
                Title = PatchField<string?>.Submitted("Renamed"),
            },
            Guid.NewGuid(),
            Ct);

        Assert.True(result.Succeeded);
        Assert.Equal("Renamed", result.Value!.Title);
        Assert.Equal("Renamed", (await ReloadAsync(id)).Title);
    }

    // --- Helpers ----------------------------------------------------------------------------------------

    private static IMediaAssetDataLayer DataLayer(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IMediaAssetDataLayer>();

    private static IMediaAssetBusiness Business(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IMediaAssetBusiness>();

    /// <summary>The merged metadata a patch of just this title would produce.</summary>
    private static MediaAssetMetadataInput Merged(MediaAsset asset, string title) =>
        MediaAssetMetadataMerge.Apply(
            asset, new MediaAssetMetadataPatchViewModel { Title = PatchField<string?>.Submitted(title) });

    private async Task<string> TokenAsync(Guid id) =>
        MediaConcurrencyToken.From((await ReloadAsync(id)).RowVersion);

    private async Task<MediaAsset> ReloadAsync(Guid id)
    {
        await using var scope = fixture.ScopeFor(SqlServerMediaFixture.WorkspaceA);

        return await SqlServerMediaFixture.Db(scope).MediaAssets
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(asset => asset.Id == id, Ct);
    }

    private async Task<List<Guid>> TagsAsync(Guid id)
    {
        await using var scope = fixture.ScopeFor(SqlServerMediaFixture.WorkspaceA);

        return await SqlServerMediaFixture.Db(scope).MediaAssetTags
            .AsNoTracking()
            .Where(tag => tag.MediaAssetId == id)
            .Select(tag => tag.WorkspaceTagId)
            .ToListAsync(Ct);
    }

    private async Task<Guid> AddAsync(
        string title,
        Guid? workspaceId = null,
        string? description = null,
        IReadOnlyList<Guid>? tagIds = null,
        DateTimeOffset? deletedAt = null)
    {
        await using var scope = fixture.ScopeFor(workspaceId ?? SqlServerMediaFixture.WorkspaceA);
        var db = SqlServerMediaFixture.Db(scope);
        var actor = Guid.NewGuid();

        var asset = new MediaAsset
        {
            Id = Guid.NewGuid(),
            Title = title,
            Description = description,
            Kind = MediaAssetKind.Original,
            CurrentVersionNumber = 1,
            DeletedAt = deletedAt,
            DeletedByMembershipId = deletedAt is null ? null : actor,
            CreatedAt = Now,
            UpdatedAt = Now,
            CreatedByMembershipId = actor,
            UpdatedByMembershipId = actor,
        };

        foreach (var tagId in tagIds ?? [])
        {
            asset.Tags.Add(new MediaAssetTag { MediaAssetId = asset.Id, WorkspaceTagId = tagId });
        }

        db.MediaAssets.Add(asset);
        await db.SaveChangesAsync(Ct);

        return asset.Id;
    }
}

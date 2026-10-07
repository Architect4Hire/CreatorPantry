using CreatorPantry.Domain.Modules.Media.Business;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Media.Data;
using CreatorPantry.Domain.Modules.Media.Facade;
using CreatorPantry.Domain.Modules.Media.Data.Entities;
using CreatorPantry.Domain.Modules.Media.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Media;

/// <summary>
/// The soft delete's concurrency ordering (DAM-005) against a real SQL Server.
/// </summary>
/// <remarks>
/// Here for the reason <see cref="MediaAssetPatchSqlServerTests"/> records: SQLite seeds a concurrency token and
/// never moves it, so a token the asset <em>used</em> to have still matches there. The one thing this prompt needs
/// that engine for is the deliberate ordering — the token is checked <strong>before</strong> the already-deleted
/// answer — which can only be seen once a first deletion has moved the token on.
/// </remarks>
public sealed class MediaAssetDeleteSqlServerTests(SqlServerMediaFixture fixture)
    : IClassFixture<SqlServerMediaFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = SqlServerMediaFixture.Now;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await using var scope = fixture.ScopeFor(SqlServerMediaFixture.WorkspaceA);

        await SqlServerMediaFixture.Db(scope).Database.ExecuteSqlRawAsync(
            """
            DELETE FROM AuditLogs;
            DELETE FROM MediaAssetTags;
            DELETE FROM MediaAssetVersions;
            DELETE FROM MediaAssets;
            """,
            Ct);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>
    /// The ordering, and the one case only a real <c>rowversion</c> can produce: a caller who decided to delete
    /// against a read that is now out of date hears about the conflict rather than being told "already deleted".
    /// </summary>
    /// <remarks>
    /// Following <c>RecipeBusiness.TransitionAsync</c>, which checks the token before its own already-there answer
    /// and says why: a creator quoting a stale token has not seen what the asset looks like now, so answering
    /// "already removed" would hide a collaborator's work from them. The consequence a client has to know is that
    /// retrying a deletion whose response was lost needs a fresh read first.
    /// </remarks>
    [Fact]
    public async Task A_stale_token_is_refused_even_though_the_asset_is_already_deleted()
    {
        var id = await AddAsync("Soda bread hero");
        var stale = await TokenAsync(id);

        // Somebody deletes it first, which moves the token.
        await using (var first = fixture.ScopeFor(SqlServerMediaFixture.WorkspaceA))
        {
            var deleted = await Business(first).SoftDeleteAsync(id, stale, "user-1", Guid.NewGuid(), Ct);

            Assert.True(deleted.Succeeded);
            Assert.False(deleted.Value!.AlreadyDeleted);
        }

        // The same token again. The asset is deleted, so without the ordering this would be a cheerful repeat.
        await using var second = fixture.ScopeFor(SqlServerMediaFixture.WorkspaceA);
        var refused = await Business(second).SoftDeleteAsync(id, stale, "user-2", Guid.NewGuid(), Ct);

        Assert.False(refused.Succeeded);
        Assert.Equal(MediaErrorCodes.AssetStaleToken, refused.Error!.Code);
    }

    /// <summary>
    /// With the current token the repeat is the no-op it should be: the original timestamp and actor come back, and
    /// nothing is written.
    /// </summary>
    [Fact]
    public async Task A_repeat_with_the_current_token_changes_nothing()
    {
        var id = await AddAsync("Soda bread hero");
        var actor = Guid.NewGuid();

        await using (var first = fixture.ScopeFor(SqlServerMediaFixture.WorkspaceA))
        {
            await Business(first).SoftDeleteAsync(id, await TokenAsync(id), "user-1", actor, Ct);
        }

        var afterFirst = await ReloadAsync(id);

        await using var second = fixture.ScopeFor(SqlServerMediaFixture.WorkspaceA);
        var repeat = await Business(second).SoftDeleteAsync(
            id, MediaConcurrencyToken.From(afterFirst.RowVersion), "user-2", Guid.NewGuid(), Ct);

        Assert.True(repeat.Succeeded);
        Assert.True(repeat.Value!.AlreadyDeleted);
        Assert.Equal(afterFirst.DeletedAt, repeat.Value.DeletedAt);
        Assert.Equal(actor, repeat.Value.DeletedByMembershipId);

        var afterRepeat = await ReloadAsync(id);

        // Not written again, which the token proves on this engine: a second write would have moved it.
        Assert.Equal(
            MediaConcurrencyToken.From(afterFirst.RowVersion),
            MediaConcurrencyToken.From(afterRepeat.RowVersion));
        Assert.Equal(afterFirst.DeletedAt, afterRepeat.DeletedAt);
        Assert.Equal(afterFirst.UpdatedAt, afterRepeat.UpdatedAt);

        // And one audit entry for one deletion.
        Assert.Equal(1, await AuditAsync(id));
    }

    /// <summary>
    /// Two deletions that both read before either wrote. Business has compared tokens and both passed, so the only
    /// thing left to refuse the second is the <c>WHERE RowVersion = @original</c> EF puts in the <c>UPDATE</c> —
    /// and it must come back as the same conflict rather than as an exception.
    /// </summary>
    [Fact]
    public async Task Two_transactions_that_both_read_first_cannot_both_delete()
    {
        var id = await AddAsync("Soda bread hero");

        await using var left = fixture.ScopeFor(SqlServerMediaFixture.WorkspaceA);
        await using var right = fixture.ScopeFor(SqlServerMediaFixture.WorkspaceA);

        var leftAssets = DataLayer(left);
        var rightAssets = DataLayer(right);

        var leftAsset = await leftAssets.FindForDeleteAsync(id, Ct);
        var rightAsset = await rightAssets.FindForDeleteAsync(id, Ct);

        Assert.Equal(
            MediaConcurrencyToken.From(leftAsset!.RowVersion),
            MediaConcurrencyToken.From(rightAsset!.RowVersion));

        Assert.True(await leftAssets.SoftDeleteAsync(leftAsset, "user-1", Guid.NewGuid(), Ct));
        Assert.False(await rightAssets.SoftDeleteAsync(rightAsset, "user-2", Guid.NewGuid(), Ct));

        // One deletion happened, so one audit entry exists — the refused write took its own entry with it, because
        // IAuditWriter stages on the same context and that SaveChanges never committed.
        Assert.Equal(1, await AuditAsync(id));
    }

    /// <summary>
    /// A tombstone is found by the delete path, where the patch path's read excludes it. That asymmetry is what
    /// makes the command idempotent rather than answering 404 on a retry.
    /// </summary>
    [Fact]
    public async Task The_delete_read_finds_a_tombstone_where_the_update_read_does_not()
    {
        var id = await AddAsync("Deleted hero", deletedAt: Now);

        await using var scope = fixture.ScopeFor(SqlServerMediaFixture.WorkspaceA);
        var assets = DataLayer(scope);

        Assert.NotNull(await assets.FindForDeleteAsync(id, Ct));
        Assert.Null(await assets.FindLiveForUpdateAsync(id, Ct));
    }

    /// <summary>
    /// The other workspace's asset cannot be loaded for deletion even at this layer, where no route or policy stands
    /// in front of it: the global query filter is what makes the read null, and null is the 404 above.
    /// </summary>
    [Fact]
    public async Task The_other_workspaces_asset_cannot_be_loaded_for_deletion()
    {
        var inB = await AddAsync("B's hero", workspaceId: SqlServerMediaFixture.WorkspaceB);

        await using var fromA = fixture.ScopeFor(SqlServerMediaFixture.WorkspaceA);
        Assert.Null(await DataLayer(fromA).FindForDeleteAsync(inB, Ct));

        // B can, so the null above is the filter working rather than a seed that never landed.
        await using var fromB = fixture.ScopeFor(SqlServerMediaFixture.WorkspaceB);
        Assert.NotNull(await DataLayer(fromB).FindForDeleteAsync(inB, Ct));
    }

    /// <summary>
    /// The facade's own role gate, reached directly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This exists because weakening that gate broke no test.</strong> The route carries
    /// <c>WorkspaceEditor</c>, so a contributor is refused at the policy and the facade never runs — which means the
    /// endpoint test proving a contributor cannot delete would still pass with the facade check deleted. Two guards
    /// are right, but only one of them was covered.
    /// </para>
    /// <para>
    /// Driven through the facade with a Contributor context, which is the only way to reach the check. The asset
    /// survives, so this is a refusal rather than a failed write.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_facade_refuses_a_contributor_even_with_a_valid_confirmation_and_token()
    {
        var id = await AddAsync("Soda bread hero");
        var token = await TokenAsync(id);

        await using var scope = fixture.ScopeFor(SqlServerMediaFixture.WorkspaceA, WorkspaceRole.Contributor);
        var result = await Facade(scope).SoftDeleteAsync(
            "user-1",
            id,
            new DeleteMediaAssetViewModel { Confirmed = true, ExpectedConcurrencyToken = token },
            Ct);

        Assert.False(result.Succeeded);
        Assert.Equal(MediaErrorCodes.AssetForbidden, result.Error!.Code);
        Assert.Null((await ReloadAsync(id)).DeletedAt);
        Assert.Equal(0, await AuditAsync(id));
    }

    /// <summary>
    /// And an editor is allowed through, so the refusal above is the role and not a facade that cannot delete at all.
    /// </summary>
    [Fact]
    public async Task The_facade_allows_an_editor()
    {
        var id = await AddAsync("Soda bread hero");
        var token = await TokenAsync(id);

        await using var scope = fixture.ScopeFor(SqlServerMediaFixture.WorkspaceA, WorkspaceRole.Editor);
        var result = await Facade(scope).SoftDeleteAsync(
            "user-1",
            id,
            new DeleteMediaAssetViewModel { Confirmed = true, ExpectedConcurrencyToken = token },
            Ct);

        Assert.True(result.Succeeded);
        Assert.NotNull((await ReloadAsync(id)).DeletedAt);
    }

    // --- Helpers ----------------------------------------------------------------------------------------

    private static IMediaAssetDataLayer DataLayer(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IMediaAssetDataLayer>();

    private static IMediaAssetBusiness Business(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IMediaAssetBusiness>();

    private static IMediaAssetFacade Facade(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IMediaAssetFacade>();

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

    private async Task<int> AuditAsync(Guid id)
    {
        await using var scope = fixture.ScopeFor(SqlServerMediaFixture.WorkspaceA);

        return await SqlServerMediaFixture.Db(scope).AuditLogs
            .IgnoreQueryFilters()
            .AsNoTracking()
            .CountAsync(entry => entry.ResourceId == id.ToString("D"), Ct);
    }

    private async Task<Guid> AddAsync(
        string title, Guid? workspaceId = null, DateTimeOffset? deletedAt = null)
    {
        await using var scope = fixture.ScopeFor(workspaceId ?? SqlServerMediaFixture.WorkspaceA);
        var db = SqlServerMediaFixture.Db(scope);
        var actor = Guid.NewGuid();

        var asset = new MediaAsset
        {
            Id = Guid.NewGuid(),
            Title = title,
            Kind = MediaAssetKind.Original,
            CurrentVersionNumber = 1,
            DeletedAt = deletedAt,
            DeletedByMembershipId = deletedAt is null ? null : actor,
            CreatedAt = Now,
            UpdatedAt = Now,
            CreatedByMembershipId = actor,
            UpdatedByMembershipId = actor,
        };

        db.MediaAssets.Add(asset);
        await db.SaveChangesAsync(Ct);

        return asset.Id;
    }
}

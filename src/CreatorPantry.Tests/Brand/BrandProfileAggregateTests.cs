using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Brand.Data.Entities;
using CreatorPantry.Domain.Modules.Brand.Managers;
using CreatorPantry.Tests.Recipes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Brand;

/// <summary>
/// The brand profile's schema and isolation, over the same two-workspace SQLite fixture the recipe aggregate
/// uses. Proves the EF configuration — unique and filtered indexes, check constraints, composite keys, the
/// query filter — not that SQL Server accepts the DDL, which the migration's own verification covers.
/// </summary>
public sealed class BrandProfileAggregateTests : IDisposable
{
    private static readonly DateTimeOffset Now = RecipeAggregateFixture.Now;

    private readonly RecipeAggregateFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private static BrandProfile NewProfile(Guid workspaceId, string name = "Sam's Kitchen") => new()
    {
        Id = Guid.NewGuid(),
        WorkspaceId = workspaceId,
        BrandName = name,
        CreatedAt = Now,
        UpdatedAt = Now,
        CreatedByMembershipId = Guid.NewGuid(),
        UpdatedByMembershipId = Guid.NewGuid(),
    };

    private async Task<BrandProfile> SeedAsync(Guid workspaceId, string name = "Sam's Kitchen")
    {
        await using var scope = _fixture.ScopeFor(workspaceId);
        var db = RecipeAggregateFixture.Db(scope);
        var profile = NewProfile(workspaceId, name);
        db.BrandProfiles.Add(profile);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return profile;
    }

    [Fact]
    public async Task Each_workspace_holds_its_own_profile_and_cannot_see_the_others()
    {
        var a = await SeedAsync(RecipeAggregateFixture.WorkspaceA, "Kitchen A");
        var b = await SeedAsync(RecipeAggregateFixture.WorkspaceB, "Kitchen B");

        await using var scopeA = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var dbA = RecipeAggregateFixture.Db(scopeA);
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal(a.Id, (await dbA.BrandProfiles.SingleAsync(ct)).Id);
        Assert.Null(await dbA.BrandProfiles.FirstOrDefaultAsync(p => p.Id == b.Id, ct));
        Assert.Empty(await dbA.BrandLinks.ToListAsync(ct));
    }

    [Fact]
    public async Task A_workspace_cannot_have_two_profiles()
    {
        await SeedAsync(RecipeAggregateFixture.WorkspaceA);

        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);
        db.BrandProfiles.Add(NewProfile(RecipeAggregateFixture.WorkspaceA, "Second brand"));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_blank_brand_name_is_refused()
    {
        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);
        db.BrandProfiles.Add(NewProfile(RecipeAggregateFixture.WorkspaceA, "   "));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Revision_below_one_is_refused()
    {
        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);
        var profile = NewProfile(RecipeAggregateFixture.WorkspaceA);
        profile.Revision = 0;
        db.BrandProfiles.Add(profile);

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Children_round_trip_and_cascade_with_the_profile()
    {
        var profile = await SeedAsync(RecipeAggregateFixture.WorkspaceA);
        var ct = TestContext.Current.CancellationToken;

        await using (var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA))
        {
            var db = RecipeAggregateFixture.Db(scope);
            var ws = RecipeAggregateFixture.WorkspaceA;
            db.BrandChannelDefaults.Add(new BrandChannelDefault { Id = Guid.NewGuid(), WorkspaceId = ws, BrandProfileId = profile.Id, ChannelKey = "instagram", SortOrder = 0 });
            db.BrandLinks.Add(new BrandLink { Id = Guid.NewGuid(), WorkspaceId = ws, BrandProfileId = profile.Id, Kind = BrandLinkKind.Website, Url = "https://example.com", SortOrder = 0 });
            db.BrandAssetLinks.Add(new BrandAssetLink { Id = Guid.NewGuid(), WorkspaceId = ws, BrandProfileId = profile.Id, MediaAssetId = Guid.NewGuid(), Role = BrandAssetRole.PrimaryLogo, SortOrder = 0 });
            await db.SaveChangesAsync(ct);
        }

        await using (var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA))
        {
            var db = RecipeAggregateFixture.Db(scope);
            var loaded = await db.BrandProfiles
                .Include(p => p.ChannelDefaults).Include(p => p.Links).Include(p => p.AssetLinks)
                .SingleAsync(ct);
            Assert.Single(loaded.ChannelDefaults);
            Assert.Single(loaded.Links);
            Assert.Single(loaded.AssetLinks);

            db.BrandProfiles.Remove(loaded);
            await db.SaveChangesAsync(ct);

            Assert.Empty(await db.BrandChannelDefaults.ToListAsync(ct));
            Assert.Empty(await db.BrandLinks.ToListAsync(ct));
            Assert.Empty(await db.BrandAssetLinks.ToListAsync(ct));
        }
    }

    [Fact]
    public async Task A_child_cannot_point_at_another_workspaces_profile()
    {
        var profileB = await SeedAsync(RecipeAggregateFixture.WorkspaceB);

        // Written from workspace A's scope, naming workspace B's profile id: the composite key
        // (WorkspaceId, BrandProfileId) has no matching (WorkspaceId, Id) row, so the database refuses it.
        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);
        db.BrandLinks.Add(new BrandLink
        {
            Id = Guid.NewGuid(),
            WorkspaceId = RecipeAggregateFixture.WorkspaceA,
            BrandProfileId = profileB.Id,
            Kind = BrandLinkKind.Reference,
            Url = "https://example.com",
            SortOrder = 0,
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Channel_keys_and_sort_orders_are_unique_within_a_profile()
    {
        var profile = await SeedAsync(RecipeAggregateFixture.WorkspaceA);
        var ws = RecipeAggregateFixture.WorkspaceA;
        var ct = TestContext.Current.CancellationToken;

        await using var scope = _fixture.ScopeFor(ws);
        var db = RecipeAggregateFixture.Db(scope);
        db.BrandChannelDefaults.AddRange(
            new BrandChannelDefault { Id = Guid.NewGuid(), WorkspaceId = ws, BrandProfileId = profile.Id, ChannelKey = "instagram", SortOrder = 0 },
            new BrandChannelDefault { Id = Guid.NewGuid(), WorkspaceId = ws, BrandProfileId = profile.Id, ChannelKey = "instagram", SortOrder = 1 });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(ct));
    }

    [Fact]
    public async Task A_profile_has_at_most_one_primary_logo_but_any_number_of_alternates()
    {
        var profile = await SeedAsync(RecipeAggregateFixture.WorkspaceA);
        var ws = RecipeAggregateFixture.WorkspaceA;
        var ct = TestContext.Current.CancellationToken;

        BrandAssetLink Link(BrandAssetRole role, int order) => new()
        {
            Id = Guid.NewGuid(), WorkspaceId = ws, BrandProfileId = profile.Id,
            MediaAssetId = Guid.NewGuid(), Role = role, SortOrder = order,
        };

        await using (var scope = _fixture.ScopeFor(ws))
        {
            var db = RecipeAggregateFixture.Db(scope);
            db.BrandAssetLinks.AddRange(
                Link(BrandAssetRole.PrimaryLogo, 0), Link(BrandAssetRole.AlternateLogo, 1), Link(BrandAssetRole.AlternateLogo, 2));
            await db.SaveChangesAsync(ct);
        }

        await using (var scope = _fixture.ScopeFor(ws))
        {
            var db = RecipeAggregateFixture.Db(scope);
            db.BrandAssetLinks.Add(Link(BrandAssetRole.PrimaryLogo, 3));
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(ct));
        }
    }

    [Fact]
    public async Task An_unresolved_scope_cannot_read_profiles()
    {
        await SeedAsync(RecipeAggregateFixture.WorkspaceA);

        await using var scope = _fixture.UnresolvedScope();
        var db = RecipeAggregateFixture.Db(scope);

        await Assert.ThrowsAnyAsync<Exception>(() => db.BrandProfiles.ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Every_brand_entity_is_workspace_owned_and_filtered()
    {
        using var scope = _fixture.UnresolvedScope();
        var model = RecipeAggregateFixture.Db(scope).Model;

        foreach (var type in new[] { typeof(BrandProfile), typeof(BrandChannelDefault), typeof(BrandLink), typeof(BrandAssetLink) })
        {
            Assert.True(typeof(IWorkspaceOwned).IsAssignableFrom(type), $"{type.Name} is not IWorkspaceOwned");
            Assert.NotEmpty(model.FindEntityType(type)!.GetDeclaredQueryFilters());
        }
    }

    private async Task SeedChildrenAsync(Guid workspaceId, Guid profileId, Guid assetId)
    {
        await using var scope = _fixture.ScopeFor(workspaceId);
        var db = RecipeAggregateFixture.Db(scope);
        db.BrandChannelDefaults.Add(new BrandChannelDefault { Id = Guid.NewGuid(), WorkspaceId = workspaceId, BrandProfileId = profileId, ChannelKey = "instagram", SortOrder = 0 });
        db.BrandLinks.Add(new BrandLink { Id = Guid.NewGuid(), WorkspaceId = workspaceId, BrandProfileId = profileId, Kind = BrandLinkKind.Website, Url = "https://example.com", SortOrder = 0 });
        db.BrandAssetLinks.Add(new BrandAssetLink { Id = Guid.NewGuid(), WorkspaceId = workspaceId, BrandProfileId = profileId, MediaAssetId = assetId, Role = BrandAssetRole.PrimaryLogo, SortOrder = 0 });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Two_workspaces_hold_identical_child_values_and_each_sees_only_its_own()
    {
        var a = await SeedAsync(RecipeAggregateFixture.WorkspaceA, "Kitchen A");
        var b = await SeedAsync(RecipeAggregateFixture.WorkspaceB, "Kitchen B");
        var sharedAsset = Guid.NewGuid();
        var ct = TestContext.Current.CancellationToken;

        // Same channel key, sort order, link order and primary logo in both: uniqueness is workspace-relative.
        await SeedChildrenAsync(RecipeAggregateFixture.WorkspaceA, a.Id, sharedAsset);
        await SeedChildrenAsync(RecipeAggregateFixture.WorkspaceB, b.Id, sharedAsset);

        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);

        Assert.All(await db.BrandChannelDefaults.ToListAsync(ct), c => Assert.Equal(a.Id, c.BrandProfileId));
        Assert.All(await db.BrandLinks.ToListAsync(ct), c => Assert.Equal(a.Id, c.BrandProfileId));
        Assert.All(await db.BrandAssetLinks.ToListAsync(ct), c => Assert.Equal(a.Id, c.BrandProfileId));
        Assert.Single(await db.BrandLinks.ToListAsync(ct));

        Guid bLinkId;
        await using (var scopeB = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceB))
        {
            bLinkId = await RecipeAggregateFixture.Db(scopeB).BrandLinks.Select(l => l.Id).SingleAsync(ct);
        }

        Assert.Null(await db.BrandLinks.FirstOrDefaultAsync(l => l.Id == bLinkId, ct));
    }

    [Fact]
    public async Task A_row_stamped_with_another_workspace_from_this_scope_is_refused()
    {
        var profile = await SeedAsync(RecipeAggregateFixture.WorkspaceA);

        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);
        db.BrandLinks.Add(new BrandLink
        {
            Id = Guid.NewGuid(),
            WorkspaceId = RecipeAggregateFixture.WorkspaceB,
            BrandProfileId = profile.Id,
            Kind = BrandLinkKind.Website,
            Url = "https://example.com",
            SortOrder = 0,
        });

        await Assert.ThrowsAnyAsync<Exception>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Ownership_of_a_profile_cannot_be_changed_by_update()
    {
        await SeedAsync(RecipeAggregateFixture.WorkspaceA);

        await using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);
        var db = RecipeAggregateFixture.Db(scope);
        var profile = await db.BrandProfiles.SingleAsync(TestContext.Current.CancellationToken);
        profile.WorkspaceId = RecipeAggregateFixture.WorkspaceB;

        await Assert.ThrowsAnyAsync<Exception>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>The filtered primary-logo index hard-codes this value; reordering the enum needs a migration.</summary>
    [Fact]
    public void The_primary_logo_value_the_filtered_index_depends_on_is_pinned()
    {
        Assert.Equal(1, (int)BrandAssetRole.PrimaryLogo);
    }

    /// <summary>
    /// Voice, tone, tenor, writing style and visual direction belong to Phase 11A's
    /// <c>BrandStyleGuideVersion</c> alone (DEC-010). A second copy here would compete with it.
    /// </summary>
    [Fact]
    public void No_brand_property_carries_voice_or_visual_direction()
    {
        // Style words (DEC-010), and words that would turn a brand default into a way around platform safety:
        // a brand field describes the brand and never switches a warning, disclaimer or check on or off.
        string[] forbidden =
        [
            "voice", "tone", "tenor", "style", "visual", "palette", "color", "colour", "font", "mood",
            "safety", "allergen", "disclaimer", "warning", "override", "skip", "bypass",
        ];

        var offenders = new[]
            {
                typeof(BrandProfile), typeof(BrandChannelDefault), typeof(BrandLink), typeof(BrandAssetLink),
                typeof(BrandProfileRevision), typeof(CreateBrandProfileViewModel), typeof(UpdateBrandProfileViewModel),
                typeof(BrandProfileServiceModel), typeof(BrandLinkInput), typeof(BrandAssetInput), typeof(BrandChannelDefaultInput),
            }
            .SelectMany(type => type.GetProperties().Select(p => (Type: type.Name, p.Name)))
            .Where(p => forbidden.Any(word => p.Name.Contains(word, StringComparison.OrdinalIgnoreCase)))
            .Select(p => $"{p.Type}.{p.Name}")
            .ToList();

        Assert.True(offenders.Count == 0, "style belongs to BrandStyleGuideVersion: " + string.Join(", ", offenders));
    }
}

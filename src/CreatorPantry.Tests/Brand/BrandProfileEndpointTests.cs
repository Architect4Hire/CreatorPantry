using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Brand.Data.Entities;
using CreatorPantry.Domain.Modules.Brand.Managers;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Tests.Tenancy;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Brand;

/// <summary>
/// <c>GET .../workspaces/{slug}/brand-profile</c> through the real Gateway: found, empty, and the boundary
/// between two workspaces.
/// </summary>
public sealed class BrandProfileEndpointTests : IAsyncLifetime
{
    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync() => _fixture = await TwoWorkspaceGatewayFixture.CreateAsync();

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static string BrandIn(SeededWorkspace workspace) => $"/api/v1/workspaces/{workspace.Slug}/brand-profile";

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

    private async Task<Guid> SeedAsync(SeededWorkspace workspace, string brandName, bool withChildren = true)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspace.Id, workspace.Slug, Guid.NewGuid(), WorkspaceRole.Owner, "test-account");
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        var now = DateTimeOffset.UtcNow;
        var profile = new BrandProfile
        {
            Id = Guid.NewGuid(),
            BrandName = brandName,
            ShortDescription = "Weeknight cooking for small kitchens.",
            DefaultAudience = "Busy home cooks",
            Locale = "en-US",
            TimeZoneId = "America/Chicago",
            CreatedAt = now,
            UpdatedAt = now,
            CreatedByMembershipId = Guid.NewGuid(),
            UpdatedByMembershipId = Guid.NewGuid(),
        };

        if (withChildren)
        {
            // Inserted out of order so the assertions prove the read orders by SortOrder, not by insertion.
            profile.ChannelDefaults =
            [
                new() { Id = Guid.NewGuid(), ChannelKey = "tiktok", SortOrder = 1 },
                new() { Id = Guid.NewGuid(), ChannelKey = "instagram", SortOrder = 0 },
            ];
            profile.Links =
            [
                new() { Id = Guid.NewGuid(), Kind = BrandLinkKind.Reference, Url = "https://example.com/about", SortOrder = 1 },
                new() { Id = Guid.NewGuid(), Kind = BrandLinkKind.Website, Url = "https://example.com", Label = "Home", SortOrder = 0 },
            ];
            profile.AssetLinks =
            [
                new() { Id = Guid.NewGuid(), MediaAssetId = Guid.NewGuid(), Role = BrandAssetRole.AlternateLogo, SortOrder = 1 },
                new() { Id = Guid.NewGuid(), MediaAssetId = Guid.NewGuid(), Role = BrandAssetRole.PrimaryLogo, SortOrder = 0 },
            ];
        }

        db.BrandProfiles.Add(profile);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return profile.Id;
    }

    [Fact]
    public async Task A_workspace_with_a_profile_returns_it_complete_and_in_order()
    {
        var cancellation = TestContext.Current.CancellationToken;
        var id = await SeedAsync(_fixture.WorkspaceA, "Kitchen A");
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);

        var response = await client.GetAsync(BrandIn(_fixture.WorkspaceA), cancellation);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());

        var body = await BodyOf(response);
        Assert.Equal(id, body.GetProperty("id").GetGuid());
        Assert.Equal("Kitchen A", body.GetProperty("brandName").GetString());
        Assert.Equal("America/Chicago", body.GetProperty("timeZoneId").GetString());
        Assert.Equal(1, body.GetProperty("revision").GetInt32());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("concurrencyToken").GetString()));

        Assert.Equal(
            ["instagram", "tiktok"],
            body.GetProperty("channelDefaults").EnumerateArray().Select(c => c.GetProperty("channelKey").GetString()));
        Assert.Equal(
            ["Website", "Reference"],
            body.GetProperty("links").EnumerateArray().Select(l => l.GetProperty("kind").GetString()));
        Assert.Equal(
            ["PrimaryLogo", "AlternateLogo"],
            body.GetProperty("assets").EnumerateArray().Select(a => a.GetProperty("role").GetString()));
    }

    [Fact]
    public async Task A_viewer_may_read()
    {
        var cancellation = TestContext.Current.CancellationToken;
        await SeedAsync(_fixture.WorkspaceB, "Kitchen B");
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceB.MemberEmail, cancellationToken: cancellation);

        var response = await client.GetAsync(BrandIn(_fixture.WorkspaceB), cancellation);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_profile_with_no_children_reads_with_empty_lists()
    {
        var cancellation = TestContext.Current.CancellationToken;
        await SeedAsync(_fixture.WorkspaceA, "Bare", withChildren: false);
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);

        var body = await BodyOf(await client.GetAsync(BrandIn(_fixture.WorkspaceA), cancellation));

        Assert.Empty(body.GetProperty("channelDefaults").EnumerateArray());
        Assert.Empty(body.GetProperty("links").EnumerateArray());
        Assert.Empty(body.GetProperty("assets").EnumerateArray());
    }

    [Fact]
    public async Task A_workspace_with_no_profile_answers_the_stable_not_found_code()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);

        var response = await client.GetAsync(BrandIn(_fixture.WorkspaceA), cancellation);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await BodyOf(response);
        Assert.Equal(BrandErrorCodes.ProfileNotFound, body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Each_workspace_reads_only_its_own_profile()
    {
        var cancellation = TestContext.Current.CancellationToken;
        var idA = await SeedAsync(_fixture.WorkspaceA, "Kitchen A");
        var idB = await SeedAsync(_fixture.WorkspaceB, "Kitchen B");

        using var inA = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);
        using var inB = await _fixture.SignInAsync(_fixture.WorkspaceB.OwnerEmail, cancellationToken: cancellation);

        var a = await BodyOf(await inA.GetAsync(BrandIn(_fixture.WorkspaceA), cancellation));
        var b = await BodyOf(await inB.GetAsync(BrandIn(_fixture.WorkspaceB), cancellation));

        Assert.Equal(idA, a.GetProperty("id").GetGuid());
        Assert.Equal("Kitchen A", a.GetProperty("brandName").GetString());
        Assert.Equal(idB, b.GetProperty("id").GetGuid());
        Assert.Equal("Kitchen B", b.GetProperty("brandName").GetString());
    }

    /// <summary>
    /// A member of A reading B's route, and a route nobody owns, answer identically: same status, same code.
    /// Workspace B has a profile, so the 404 cannot be "no profile".
    /// </summary>
    [Fact]
    public async Task Another_workspaces_route_and_an_unknown_one_are_indistinguishable()
    {
        var cancellation = TestContext.Current.CancellationToken;
        await SeedAsync(_fixture.WorkspaceB, "Kitchen B");
        using var inA = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);

        var foreign = await inA.GetAsync(BrandIn(_fixture.WorkspaceB), cancellation);
        var unknown = await inA.GetAsync("/api/v1/workspaces/no-such-workspace/brand-profile", cancellation);

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        // The whole problem body, not just the code: a detail that echoed the slug or a workspace name would
        // be a disclosure this comparison would otherwise miss. Correlation and instance vary per request.
        var foreignBody = await BodyOf(foreign);
        var unknownBody = await BodyOf(unknown);
        foreach (var field in new[] { "code", "title", "detail", "status", "type" })
        {
            Assert.Equal(
                unknownBody.TryGetProperty(field, out var expected) ? expected.ToString() : null,
                foreignBody.TryGetProperty(field, out var actual) ? actual.ToString() : null);
        }
    }

    /// <summary>
    /// Only B has a profile. A's owner reads A's own route and finds nothing, so B's row is hidden by the
    /// filter rather than the isolation test depending on both workspaces having one.
    /// </summary>
    [Fact]
    public async Task A_profile_in_another_workspace_does_not_satisfy_this_workspaces_read()
    {
        var cancellation = TestContext.Current.CancellationToken;
        await SeedAsync(_fixture.WorkspaceB, "Kitchen B");
        using var inA = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);

        var response = await inA.GetAsync(BrandIn(_fixture.WorkspaceA), cancellation);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(BrandErrorCodes.ProfileNotFound, (await BodyOf(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task The_payload_exposes_no_workspace_or_membership_identifiers()
    {
        var cancellation = TestContext.Current.CancellationToken;
        await SeedAsync(_fixture.WorkspaceA, "Kitchen A");
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: cancellation);

        var body = await BodyOf(await client.GetAsync(BrandIn(_fixture.WorkspaceA), cancellation));
        var names = body.EnumerateObject().Select(p => p.Name).ToList();

        Assert.DoesNotContain(names, n => n.Contains("workspace", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, n => n.Contains("membership", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task An_anonymous_caller_is_refused()
    {
        using var client = _fixture.Gateway.CreateClient();

        var response = await client.GetAsync(BrandIn(_fixture.WorkspaceA), TestContext.Current.CancellationToken);

        Assert.True(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Redirect or HttpStatusCode.Found);
    }
}

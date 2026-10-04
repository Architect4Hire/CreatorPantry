using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Content.Managers;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Domain.Modules.Vocabulary.Data.Entities;
using CreatorPantry.Tests.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Content;

/// <summary>
/// <c>GET .../content-seeds</c> through the real Gateway: the contract, reproducibility over the wire, refusals,
/// and that a seed is a read rather than a write.
/// </summary>
/// <remarks>
/// <para>
/// <c>SqliteApiHost</c> does not run the reference seeder, so the three database-backed facets are seeded here.
/// Their absence is not a gap in the host: it showed that an empty catalogue degrades to a facet the seed omits
/// rather than an error, which <c>ContentSeedBusinessTests</c> now covers deliberately. These rows are the keys
/// the assertions below pin, not the shipped list.
/// </para>
/// </remarks>
public sealed class ContentSeedEndpointTests : IAsyncLifetime
{
    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync()
    {
        _fixture = await TwoWorkspaceGatewayFixture.CreateAsync();

        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        db.Cuisines.AddRange(
            Vocabulary<Cuisine>("thai", "Thai"),
            Vocabulary<Cuisine>("italian", "Italian"));
        db.Courses.AddRange(
            Vocabulary<Course>("main-course", "Main Course"),
            Vocabulary<Course>("dessert", "Dessert"));
        db.CookingTechniques.AddRange(
            new CookingTechnique { Id = Guid.NewGuid(), Code = "bake", DisplayName = "Bake", IsActive = true },
            new CookingTechnique
            {
                Id = Guid.NewGuid(),
                Code = "ferment",
                DisplayName = "Ferment",
                IsActive = true,
                RequiresSafetyCaution = true,
            });

        await db.SaveChangesAsync(Ct);
    }

    private static T Vocabulary<T>(string code, string displayName)
        where T : ControlledVocabulary, new() =>
        new() { Id = Guid.NewGuid(), Code = code, DisplayName = displayName, IsActive = true };

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string SeedsIn(SeededWorkspace workspace, string query = "") =>
        $"/api/v1/workspaces/{workspace.Slug}/content-seeds{query}";

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    private static string Code(JsonElement body) => body.GetProperty("code").GetString()!;

    private static string? FacetKey(JsonElement body, string facet) =>
        body.GetProperty(facet) is { ValueKind: JsonValueKind.Object } value
            ? value.GetProperty("key").GetString()
            : null;

    private Task<GatewayClient> SignInAsync(string email) => _fixture.SignInAsync(email, cancellationToken: Ct);

    [Fact]
    public async Task A_seed_arrives_complete_and_carries_its_token()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var response = await client.GetAsync(SeedsIn(_fixture.WorkspaceA), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());

        var body = await BodyOf(response);
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("token").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("description").GetString()));

        foreach (var facet in new[] { "cuisine", "dishType", "method", "photographyStyle", "channel", "occasion" })
        {
            Assert.Equal(JsonValueKind.Object, body.GetProperty(facet).ValueKind);
            Assert.False(string.IsNullOrWhiteSpace(FacetKey(body, facet)));
        }

        // The day is always present; its theme is not, and a fresh workspace has none.
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("day").GetProperty("day").GetString()));
        Assert.Equal(JsonValueKind.Null, body.GetProperty("day").GetProperty("theme").ValueKind);

        // The safety flag is part of the method's shape whether or not this particular method sets it, so a client
        // can always read it rather than having to know which methods carry one.
        Assert.Contains(
            body.GetProperty("method").GetProperty("requiresSafetyCaution").ValueKind,
            new[] { JsonValueKind.True, JsonValueKind.False });
    }

    [Fact]
    public async Task Returning_a_token_reproduces_the_seed()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var first = await BodyOf(await client.GetAsync(SeedsIn(_fixture.WorkspaceA), Ct));
        var token = first.GetProperty("token").GetString()!;

        var again = await BodyOf(await client.GetAsync(SeedsIn(_fixture.WorkspaceA, $"?token={token}"), Ct));

        Assert.Equal(token, again.GetProperty("token").GetString());
        Assert.Equal(first.GetProperty("description").GetString(), again.GetProperty("description").GetString());
        foreach (var facet in new[] { "cuisine", "dishType", "method", "photographyStyle", "channel", "occasion" })
        {
            Assert.Equal(FacetKey(first, facet), FacetKey(again, facet));
        }
    }

    [Fact]
    public async Task A_creators_own_token_is_accepted()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var body = await BodyOf(await client.GetAsync(SeedsIn(_fixture.WorkspaceA, "?token=spring-bakes"), Ct));

        Assert.Equal("spring-bakes", body.GetProperty("token").GetString());
    }

    [Fact]
    public async Task Pinned_facets_come_back_pinned()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var body = await BodyOf(await client.GetAsync(
            SeedsIn(_fixture.WorkspaceA, "?cuisine=thai&dishType=dessert&method=bake&day=Saturday&occasion=weeknight"),
            Ct));

        Assert.Equal("thai", FacetKey(body, "cuisine"));
        Assert.Equal("dessert", FacetKey(body, "dishType"));
        Assert.Equal("bake", FacetKey(body, "method"));
        Assert.Equal("weeknight", FacetKey(body, "occasion"));
        Assert.Equal("Saturday", body.GetProperty("day").GetProperty("day").GetString());

        Assert.True(body.GetProperty("cuisine").GetProperty("pinned").GetBoolean());
        Assert.True(body.GetProperty("day").GetProperty("pinned").GetBoolean());

        // The facets nobody pinned say so.
        Assert.False(body.GetProperty("photographyStyle").GetProperty("pinned").GetBoolean());
    }

    [Fact]
    public async Task An_invalid_channel_fails()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var response = await client.GetAsync(SeedsIn(_fixture.WorkspaceA, "?channel=myspace"), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await BodyOf(response);
        Assert.Equal(ContentErrorCodes.ContentSeedInvalid, Code(body));
        Assert.True(body.GetProperty("errors").TryGetProperty("channel", out _));
    }

    [Fact]
    public async Task An_invalid_pinned_vocabulary_key_fails_with_its_own_field()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var response = await client.GetAsync(SeedsIn(_fixture.WorkspaceA, "?cuisine=klingon&method=sonic-blast"), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await BodyOf(response)).GetProperty("errors");
        Assert.True(errors.TryGetProperty("cuisine", out _));
        Assert.True(errors.TryGetProperty("method", out _));
    }

    [Fact]
    public async Task A_malformed_token_fails()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var response = await client.GetAsync(SeedsIn(_fixture.WorkspaceA, "?token=not%20a%20token"), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await BodyOf(response)).GetProperty("errors").TryGetProperty("token", out _));
    }

    [Fact]
    public async Task A_day_that_is_not_a_day_fails()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var response = await client.GetAsync(SeedsIn(_fixture.WorkspaceA, "?day=Someday"), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_viewer_may_read_a_seed()
    {
        // Nothing is written and it composes only data a Viewer can already read, so a Viewer may ask for one.
        using var viewer = await SignInAsync(_fixture.WorkspaceB.MemberEmail);

        var response = await viewer.GetAsync(SeedsIn(_fixture.WorkspaceB), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task The_workspaces_own_weekly_theme_reaches_its_seed_and_no_others()
    {
        using var ownerB = await SignInAsync(_fixture.WorkspaceB.OwnerEmail);
        await ownerB.SendAsync(
            HttpMethod.Put,
            $"/api/v1/workspaces/{_fixture.WorkspaceB.Slug}/weekly-themes",
            new { themes = new[] { new { day = "Tuesday", key = "taco-tuesday", displayName = "B's Taco Tuesday" } } },
            headers: null,
            Ct);

        var fromB = await BodyOf(await ownerB.GetAsync(SeedsIn(_fixture.WorkspaceB, "?day=Tuesday"), Ct));
        Assert.Equal("taco-tuesday", fromB.GetProperty("day").GetProperty("theme").GetProperty("key").GetString());
        Assert.Contains("B's Taco Tuesday", fromB.GetProperty("description").GetString()!);

        using var ownerA = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        var fromA = await BodyOf(await ownerA.GetAsync(SeedsIn(_fixture.WorkspaceA, "?day=Tuesday"), Ct));
        Assert.Equal(JsonValueKind.Null, fromA.GetProperty("day").GetProperty("theme").ValueKind);
        Assert.DoesNotContain("Taco", fromA.GetProperty("description").GetString()!);
    }

    [Fact]
    public async Task A_member_of_one_workspace_cannot_seed_against_the_others_route()
    {
        using var ownerA = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var response = await ownerA.GetAsync(SeedsIn(_fixture.WorkspaceB), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Generating_seeds_writes_nothing()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);
        await client.GetAsync(SeedsIn(_fixture.WorkspaceA), Ct);
        await client.GetAsync(SeedsIn(_fixture.WorkspaceA, "?token=spring-bakes&cuisine=thai"), Ct);
        await client.GetAsync(SeedsIn(_fixture.WorkspaceA, "?cuisine=klingon"), Ct);

        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            _fixture.WorkspaceA.Id, _fixture.WorkspaceA.Slug, Guid.NewGuid(), WorkspaceRole.Owner, "test-account");
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        // A seed is an idea, not an event: no audit row, no idempotency record, no outbox message.
        Assert.Empty(await db.AuditLogs.ToListAsync(Ct));
        Assert.Empty(await db.IdempotencyRecords.ToListAsync(Ct));
        Assert.Empty(await db.OutboxMessages.ToListAsync(Ct));
    }

    [Fact]
    public async Task A_safety_critical_method_arrives_with_its_caution()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA.OwnerEmail);

        var body = await BodyOf(await client.GetAsync(SeedsIn(_fixture.WorkspaceA, "?method=ferment"), Ct));

        Assert.Equal("ferment", FacetKey(body, "method"));
        Assert.True(body.GetProperty("method").GetProperty("requiresSafetyCaution").GetBoolean());
    }
}

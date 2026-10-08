using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Tests.Tenancy;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Recipes;

/// <summary>
/// <c>GET /api/v1/workspaces/{workspaceSlug}/tags</c> through the real Gateway — real cookie session, real
/// gateway-signed internal token, real API — for the published shape, the ordering, what is left out, the cap,
/// and what a member of one workspace can learn about the other's vocabulary (12.10g).
/// </summary>
public sealed class WorkspaceTagsEndpointTests : IAsyncLifetime
{
    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync() => _fixture = await TwoWorkspaceGatewayFixture.CreateAsync();

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static string TagsIn(SeededWorkspace workspace) => $"/api/v1/workspaces/{workspace.Slug}/tags";

    [Fact]
    public async Task A_workspace_with_no_tags_answers_an_empty_list_rather_than_a_not_found()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);

        var response = await client.GetAsync(TagsIn(_fixture.WorkspaceA), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty((await ReadAsync(response)).EnumerateArray());
    }

    /// <summary>
    /// The whole published item is an id and the creator's own spelling. No workspace id, no normalized name
    /// and no active flag: the first never leaves the server, the second is the server's matching key, and the
    /// third is always true of a list that offers only what may be chosen.
    /// </summary>
    [Fact]
    public async Task A_tag_is_published_as_its_id_and_its_name_and_nothing_else()
    {
        var id = await SeedTagAsync(_fixture.WorkspaceA, "Freezer Friendly");

        using var client = await SignInAsync(_fixture.WorkspaceA);
        var item = (await ReadAsync(await client.GetAsync(TagsIn(_fixture.WorkspaceA), Ct))).EnumerateArray().Single();

        Assert.Equal(id, item.GetProperty("id").GetGuid());
        Assert.Equal("Freezer Friendly", item.GetProperty("name").GetString());
        Assert.Equal(["id", "name"], item.EnumerateObject().Select(property => property.Name).Order());
    }

    [Fact]
    public async Task Tags_are_listed_by_name_whatever_their_case_or_the_order_they_were_made_in()
    {
        await SeedTagAsync(_fixture.WorkspaceA, "weeknight");
        await SeedTagAsync(_fixture.WorkspaceA, "Baking");
        await SeedTagAsync(_fixture.WorkspaceA, "autumn");

        using var client = await SignInAsync(_fixture.WorkspaceA);

        Assert.Equal(["autumn", "Baking", "weeknight"], await NamesAsync(client, _fixture.WorkspaceA));
    }

    /// <summary>
    /// A retired tag leaves the pickers. It stays named on whatever already carries it, through that record's
    /// own read, so nothing is lost by leaving it out here.
    /// </summary>
    [Fact]
    public async Task A_retired_tag_is_not_offered()
    {
        await SeedTagAsync(_fixture.WorkspaceA, "Current");
        await SeedTagAsync(_fixture.WorkspaceA, "Retired", isActive: false);

        using var client = await SignInAsync(_fixture.WorkspaceA);

        Assert.Equal(["Current"], await NamesAsync(client, _fixture.WorkspaceA));
    }

    /// <summary>
    /// The list is not paged, so it has a ceiling: past it the vocabulary is cut alphabetically rather than
    /// returned whole. The same cut every time, because the ordering is total.
    /// </summary>
    [Fact]
    public async Task The_list_is_capped_and_the_cut_is_alphabetical()
    {
        await SeedTagsAsync(
            _fixture.WorkspaceA,
            [.. Enumerable.Range(0, WorkspaceTagPolicy.MaxListed + 5).Select(index => $"tag-{index:D4}")]);

        using var client = await SignInAsync(_fixture.WorkspaceA);
        var names = await NamesAsync(client, _fixture.WorkspaceA);

        Assert.Equal(WorkspaceTagPolicy.MaxListed, names.Count);
        Assert.Equal("tag-0000", names[0]);
        Assert.Equal($"tag-{WorkspaceTagPolicy.MaxListed - 1:D4}", names[^1]);
    }

    [Fact]
    public async Task The_response_is_not_stored()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);

        var response = await client.GetAsync(TagsIn(_fixture.WorkspaceA), Ct);

        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    // ---- Isolation ----

    /// <summary>
    /// Workspace A and Workspace B each hold a tag named "Weeknight" and one of their own — the two-workspace
    /// coverage tenancy.md requires. Each sees its own two, and the identically named tags are different rows.
    /// </summary>
    [Fact]
    public async Task Each_workspace_lists_only_its_own_vocabulary()
    {
        var weeknightInA = await SeedTagAsync(_fixture.WorkspaceA, "Weeknight");
        await SeedTagAsync(_fixture.WorkspaceA, "Only in A");
        var weeknightInB = await SeedTagAsync(_fixture.WorkspaceB, "Weeknight");
        await SeedTagAsync(_fixture.WorkspaceB, "Only in B");

        using var ownerA = await SignInAsync(_fixture.WorkspaceA);
        using var ownerB = await SignInAsync(_fixture.WorkspaceB);

        var fromA = await ReadAsync(await ownerA.GetAsync(TagsIn(_fixture.WorkspaceA), Ct));
        var fromB = await ReadAsync(await ownerB.GetAsync(TagsIn(_fixture.WorkspaceB), Ct));

        Assert.Equal(["Only in A", "Weeknight"], Names(fromA));
        Assert.Equal(["Only in B", "Weeknight"], Names(fromB));
        Assert.Contains(weeknightInA, Ids(fromA));
        Assert.DoesNotContain(weeknightInB, Ids(fromA));
        Assert.DoesNotContain(weeknightInA, Ids(fromB));
    }

    /// <summary>
    /// A member of A asking for B's vocabulary is told the workspace is not there — the same answer an unknown
    /// slug gets — rather than being refused in a way that confirms B exists.
    /// </summary>
    [Fact]
    public async Task A_member_of_one_workspace_cannot_read_the_others_vocabulary()
    {
        await SeedTagAsync(_fixture.WorkspaceB, "Only in B");

        using var ownerA = await SignInAsync(_fixture.WorkspaceA);

        var other = await ownerA.GetAsync(TagsIn(_fixture.WorkspaceB), Ct);
        var unknown = await ownerA.GetAsync("/api/v1/workspaces/no-such-workspace/tags", Ct);

        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.DoesNotContain("Only in B", await other.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    // ---- Helpers ----

    private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<GatewayClient> SignInAsync(SeededWorkspace workspace) =>
        _fixture.SignInAsync(workspace.OwnerEmail, cancellationToken: Ct);

    private async Task<Guid> SeedTagAsync(SeededWorkspace workspace, string name, bool isActive = true) =>
        (await SeedTagsAsync(workspace, [name], isActive)).Single();

    /// <summary>
    /// Seeds tags straight into the database, with the workspace context resolved first so the ownership
    /// interceptor stamps <c>WorkspaceId</c> exactly as a request would rather than the test assigning it.
    /// </summary>
    private async Task<IReadOnlyList<Guid>> SeedTagsAsync(
        SeededWorkspace workspace, IReadOnlyList<string> names, bool isActive = true)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();

        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspace.Id, workspace.Slug, Guid.NewGuid(), WorkspaceRole.Owner, "seed");

        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var tags = names
            .Select(name => new WorkspaceTag
            {
                Id = Guid.NewGuid(),
                Name = name,
                NormalizedName = name.ToLowerInvariant(),
                IsActive = isActive,
                CreatedAt = Now,
            })
            .ToList();

        db.WorkspaceTags.AddRange(tags);
        await db.SaveChangesAsync(Ct);

        return [.. tags.Select(tag => tag.Id)];
    }

    private async Task<IReadOnlyList<string>> NamesAsync(GatewayClient client, SeededWorkspace workspace) =>
        Names(await ReadAsync(await client.GetAsync(TagsIn(workspace), Ct)));

    private static IReadOnlyList<string> Names(JsonElement body) =>
        [.. body.EnumerateArray().Select(item => item.GetProperty("name").GetString()!)];

    private static IEnumerable<Guid> Ids(JsonElement body) =>
        body.EnumerateArray().Select(item => item.GetProperty("id").GetGuid());

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
}

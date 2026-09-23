using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Tests.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Recipes;

/// <summary>
/// <c>POST /api/v1/workspaces/{workspaceSlug}/recipes</c> through the real Gateway — real cookie session,
/// real gateway-signed internal token, real API — for each role, for a replayed key, for a bad body, and
/// across two workspaces.
/// </summary>
public sealed class RecipeEndpointTests : IAsyncLifetime
{
    private const string ContributorEmail = "contributor-a@example.com";
    private const string Password = "correct horse battery";

    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync()
    {
        _fixture = await TwoWorkspaceGatewayFixture.CreateAsync();

        // The shared fixture seeds an Owner plus one lower-role member per workspace — Editor in A, Viewer in
        // B — and its own documentation says a feature needing another role combination should not stretch it.
        // Contributor is exactly the boundary this endpoint turns on, so it is added here rather than by
        // widening a fixture every other feature depends on.
        await AddMemberAsync(_fixture.WorkspaceA.Id, ContributorEmail, WorkspaceRole.Contributor);
    }

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static object ValidBody(string title = "Olive oil cake") => new { title };

    private string RecipesIn(SeededWorkspace workspace) => $"/api/v1/workspaces/{workspace.Slug}/recipes";

    // ---- Roles ----

    [Fact]
    public async Task An_owner_can_create_a_recipe()
    {
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: TestContext.Current.CancellationToken);

        var response = await client.PostAsJsonAsync(RecipesIn(_fixture.WorkspaceA), ValidBody(), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task A_contributor_can_create_a_recipe()
    {
        using var client = await _fixture.SignInAsync(ContributorEmail, cancellationToken: TestContext.Current.CancellationToken);

        var response = await client.PostAsJsonAsync(RecipesIn(_fixture.WorkspaceA), ValidBody(), TestContext.Current.CancellationToken);

        // Contributor is the lowest role permitted, so this is the half of the boundary that must succeed.
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task A_viewer_cannot_create_a_recipe()
    {
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceB.MemberEmail, cancellationToken: TestContext.Current.CancellationToken);

        var response = await client.PostAsJsonAsync(RecipesIn(_fixture.WorkspaceB), ValidBody(), TestContext.Current.CancellationToken);

        // And the half that must not. Workspace B's second member is a Viewer.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, await RecipeCountAsync(_fixture.WorkspaceB.Id));
    }

    // ---- Response shape ----

    [Fact]
    public async Task A_created_recipe_reports_its_location_and_first_version()
    {
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: TestContext.Current.CancellationToken);

        var response = await client.PostAsJsonAsync(RecipesIn(_fixture.WorkspaceA), ValidBody("Olive oil cake"), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var recipeId = body.GetProperty("recipeId").GetGuid();

        Assert.Equal($"{RecipesIn(_fixture.WorkspaceA)}/{recipeId}", response.Headers.Location!.ToString());
        Assert.Equal("Olive oil cake", body.GetProperty("title").GetString());
        Assert.Equal(1, body.GetProperty("versionNumber").GetInt32());
        Assert.NotEqual(Guid.Empty, body.GetProperty("versionId").GetGuid());
    }

    [Fact]
    public async Task The_response_carries_no_workspace_owner_or_audit_field()
    {
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: TestContext.Current.CancellationToken);

        var response = await client.PostAsJsonAsync(RecipesIn(_fixture.WorkspaceA), ValidBody(), TestContext.Current.CancellationToken);
        var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        // The request cannot carry these; neither should the reply hand them back.
        Assert.DoesNotContain("workspaceId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("membership", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rowVersion", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_client_supplied_workspace_or_owner_field_is_ignored()
    {
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: TestContext.Current.CancellationToken);

        var response = await client.PostAsJsonAsync(RecipesIn(_fixture.WorkspaceA), new
        {
            title = "Smuggled",
            workspaceId = _fixture.WorkspaceB.Id,
            createdByMembershipId = Guid.NewGuid(),
            versionNumber = 99,
        }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        // The unbound fields changed nothing: the recipe belongs to the workspace in the route, and its
        // version is 1 regardless of what the body claimed.
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(1, body.GetProperty("versionNumber").GetInt32());
        Assert.Equal(1, await RecipeCountAsync(_fixture.WorkspaceA.Id));
        Assert.Equal(0, await RecipeCountAsync(_fixture.WorkspaceB.Id));
    }

    // ---- Invalid body ----

    [Fact]
    public async Task A_recipe_with_no_title_is_refused()
    {
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: TestContext.Current.CancellationToken);

        var response = await client.PostAsJsonAsync(RecipesIn(_fixture.WorkspaceA), new { title = "   " }, TestContext.Current.CancellationToken);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("recipes.recipe.invalid_request", problem.GetProperty("code").GetString());
        Assert.True(problem.GetProperty("errors").TryGetProperty("title", out _));
        Assert.Equal(0, await RecipeCountAsync(_fixture.WorkspaceA.Id));
    }

    [Fact]
    public async Task An_unknown_cuisine_is_a_field_error_rather_than_a_server_error()
    {
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: TestContext.Current.CancellationToken);

        var response = await client.PostAsJsonAsync(
            RecipesIn(_fixture.WorkspaceA), new { title = "Cake", cuisineId = Guid.NewGuid() }, TestContext.Current.CancellationToken);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        // Without the facade's cross-module check this would reach the foreign key and surface as a 500.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(problem.GetProperty("errors").TryGetProperty("cuisineId", out _));
    }

    // ---- Idempotency ----

    [Fact]
    public async Task A_replayed_key_returns_the_original_recipe_and_creates_no_second_one()
    {
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: TestContext.Current.CancellationToken);
        var key = Guid.NewGuid().ToString();

        var first = await client.PostAsJsonAsync(RecipesIn(_fixture.WorkspaceA), ValidBody(), key, TestContext.Current.CancellationToken);
        var second = await client.PostAsJsonAsync(RecipesIn(_fixture.WorkspaceA), ValidBody(), key, TestContext.Current.CancellationToken);

        var firstId = (await first.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("recipeId").GetGuid();
        var secondId = (await second.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("recipeId").GetGuid();

        Assert.Equal(firstId, secondId);
        Assert.True(second.Headers.Contains(IdempotencyPolicy.ReplayedHeader));
        Assert.Equal(1, await RecipeCountAsync(_fixture.WorkspaceA.Id));
        Assert.Equal(1, await VersionCountAsync(firstId));
    }

    [Fact]
    public async Task A_reordered_but_equivalent_replay_is_still_a_replay()
    {
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: TestContext.Current.CancellationToken);
        var key = Guid.NewGuid().ToString();

        // The same recipe written differently: tags in another order, padded title, status stated explicitly.
        // Canonicalizing the fingerprint is what keeps this a replay instead of a spurious conflict.
        var first = await client.PostAsJsonAsync(
            RecipesIn(_fixture.WorkspaceA), new { title = "Cake", tags = new[] { "weeknight", "freezer" } }, key, TestContext.Current.CancellationToken);
        var second = await client.PostAsJsonAsync(
            RecipesIn(_fixture.WorkspaceA), new { title = "  Cake  ", tags = new[] { "freezer", "weeknight" }, status = "Draft" }, key, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.True(second.Headers.Contains(IdempotencyPolicy.ReplayedHeader));
        Assert.Equal(1, await RecipeCountAsync(_fixture.WorkspaceA.Id));
    }

    [Fact]
    public async Task Reusing_a_key_for_a_different_recipe_is_refused()
    {
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: TestContext.Current.CancellationToken);
        var key = Guid.NewGuid().ToString();

        await client.PostAsJsonAsync(RecipesIn(_fixture.WorkspaceA), ValidBody("Olive oil cake"), key, TestContext.Current.CancellationToken);
        var reused = await client.PostAsJsonAsync(RecipesIn(_fixture.WorkspaceA), ValidBody("A different cake"), key, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, reused.StatusCode);
        Assert.Equal(1, await RecipeCountAsync(_fixture.WorkspaceA.Id));
    }

    // ---- Two workspaces ----

    [Fact]
    public async Task A_member_of_one_workspace_cannot_create_in_the_other()
    {
        using var client = await _fixture.SignInAsync(ContributorEmail, cancellationToken: TestContext.Current.CancellationToken);

        var response = await client.PostAsJsonAsync(RecipesIn(_fixture.WorkspaceB), ValidBody(), TestContext.Current.CancellationToken);

        // 404, not 403: a non-member must not learn that Workspace B exists (tenancy.md).
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, await RecipeCountAsync(_fixture.WorkspaceB.Id));
    }

    [Fact]
    public async Task Each_workspace_keeps_its_own_recipes()
    {
        using var ownerA = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: TestContext.Current.CancellationToken);
        using var ownerB = await _fixture.SignInAsync(_fixture.WorkspaceB.OwnerEmail, cancellationToken: TestContext.Current.CancellationToken);

        // The same title in both, so only ownership can distinguish them.
        await ownerA.PostAsJsonAsync(RecipesIn(_fixture.WorkspaceA), ValidBody("Shared title"), TestContext.Current.CancellationToken);
        await ownerB.PostAsJsonAsync(RecipesIn(_fixture.WorkspaceB), ValidBody("Shared title"), TestContext.Current.CancellationToken);

        Assert.Equal(1, await RecipeCountAsync(_fixture.WorkspaceA.Id));
        Assert.Equal(1, await RecipeCountAsync(_fixture.WorkspaceB.Id));
    }

    [Fact]
    public async Task An_idempotency_key_does_not_carry_across_workspaces()
    {
        using var ownerA = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: TestContext.Current.CancellationToken);
        using var ownerB = await _fixture.SignInAsync(_fixture.WorkspaceB.OwnerEmail, cancellationToken: TestContext.Current.CancellationToken);
        var key = Guid.NewGuid().ToString();

        var inA = await ownerA.PostAsJsonAsync(RecipesIn(_fixture.WorkspaceA), ValidBody(), key, TestContext.Current.CancellationToken);
        var inB = await ownerB.PostAsJsonAsync(RecipesIn(_fixture.WorkspaceB), ValidBody(), key, TestContext.Current.CancellationToken);

        // The key is scoped to workspace and caller, so the same string in another workspace is a new request
        // rather than a replay of someone else's.
        Assert.Equal(HttpStatusCode.Created, inB.StatusCode);
        Assert.False(inB.Headers.Contains(IdempotencyPolicy.ReplayedHeader));
        Assert.NotEqual(
            (await inA.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("recipeId").GetGuid(),
            (await inB.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("recipeId").GetGuid());
    }

    private async Task AddMemberAsync(Guid workspaceId, string email, WorkspaceRole role)
    {
        var userId = await _fixture.Api.CreateUserAsync(email, Password);

        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        db.WorkspaceMemberships.Add(new WorkspaceMembership
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            UserId = userId,
            Role = role,
            Status = WorkspaceMembershipStatus.Active,
            JoinedAt = DateTimeOffset.UtcNow,
        });

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Counts by workspace id directly, bypassing the query filter's ambient scope.
    /// </summary>
    /// <remarks>
    /// The assertion needs ground truth about which workspace a row landed in, which is exactly what a
    /// scoped read cannot tell it — a filtered query that returns nothing proves the filter works, not that
    /// the row is absent. An explicit predicate is the honest instrument here.
    /// </remarks>
    private async Task<int> RecipeCountAsync(Guid workspaceId)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        return await db.Recipes.IgnoreQueryFilters().CountAsync(recipe => recipe.WorkspaceId == workspaceId);
    }

    private async Task<int> VersionCountAsync(Guid recipeId)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        return await db.RecipeVersions.IgnoreQueryFilters().CountAsync(version => version.RecipeId == recipeId);
    }
}

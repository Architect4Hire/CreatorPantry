using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Tests.Gateway;
using CreatorPantry.Tests.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Recipes;

/// <summary>
/// <c>GET /api/v1/workspaces/{workspaceSlug}/recipes/{recipeId}</c> through the real Gateway — real cookie
/// session, real gateway-signed internal token, real API — for what a reader sees, what it must never see, and
/// what happens when the recipe belongs to the other workspace.
/// </summary>
public sealed class RecipeDetailEndpointTests : IAsyncLifetime
{
    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync() => _fixture = await TwoWorkspaceGatewayFixture.CreateAsync();

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static string RecipeIn(SeededWorkspace workspace, Guid recipeId) =>
        $"/api/v1/workspaces/{workspace.Slug}/recipes/{recipeId}";

    // ---- What a reader sees ----

    [Fact]
    public async Task A_created_recipe_can_be_read_back_at_its_location()
    {
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: TestContext.Current.CancellationToken);

        var created = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{_fixture.WorkspaceA.Slug}/recipes",
            new { title = "Olive oil cake", tags = new[] { "weeknight" } },
            TestContext.Current.CancellationToken);
        var location = created.Headers.Location!.ToString();

        var response = await client.GetAsync(location, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        // The location a create advertises is a location that answers, which is the contract a 201 makes.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Olive oil cake", body.GetProperty("title").GetString());
        Assert.Equal("Draft", body.GetProperty("status").GetString());
        Assert.Equal(1, body.GetProperty("currentVersion").GetProperty("versionNumber").GetInt32());
        Assert.Equal("weeknight", body.GetProperty("tags")[0].GetProperty("name").GetString());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("concurrencyToken").GetString()));
    }

    [Fact]
    public async Task A_recipe_with_content_is_published_whole_and_in_order()
    {
        var recipeId = await SeedFullRecipeAsync(_fixture.WorkspaceA);

        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: TestContext.Current.CancellationToken);
        var response = await client.GetAsync(RecipeIn(_fixture.WorkspaceA, recipeId), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var groups = body.GetProperty("ingredientGroups");
        Assert.Equal(2, groups.GetArrayLength());

        // Seeded out of order, so a response in insertion order rather than sort order is visible here.
        Assert.Equal((int[])[0, 1], groups.EnumerateArray().Select(group => group.GetProperty("sortOrder").GetInt32()));
        Assert.Equal(
            (int[])[0, 1, 2],
            groups[0].GetProperty("ingredients").EnumerateArray().Select(line => line.GetProperty("sortOrder").GetInt32()));

        Assert.Equal(2, body.GetProperty("instructionGroups").GetArrayLength());
        Assert.Equal(2, body.GetProperty("equipment").GetArrayLength());
        Assert.Equal(2, body.GetProperty("assetLinks").GetArrayLength());

        // Enums as the names the schema publishes, not as integers a client would have to map privately.
        Assert.Equal("Hero", body.GetProperty("assetLinks")[0].GetProperty("role").GetString());
        Assert.Equal("NoMatch", groups[0].GetProperty("ingredients")[0].GetProperty("matchStatus").GetString());
    }

    [Fact]
    public async Task The_response_carries_no_ownership_authorship_or_storage_detail()
    {
        var recipeId = await SeedFullRecipeAsync(_fixture.WorkspaceA);

        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: TestContext.Current.CancellationToken);
        var response = await client.GetAsync(RecipeIn(_fixture.WorkspaceA, recipeId), TestContext.Current.CancellationToken);
        var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain("workspaceId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("membership", json, StringComparison.OrdinalIgnoreCase);

        // The row version reaches the client only as the opaque concurrencyToken.
        Assert.DoesNotContain("rowVersion", json, StringComparison.OrdinalIgnoreCase);

        // An asset link is a reference and nothing more: issuing access to bytes belongs to the media seam, per
        // request and with a short life. Asserted as the exact property set rather than by searching the whole
        // body for "storage" or "://" — sourceUrl and storageNotes are creator fields this read is supposed to
        // publish, so a keyword sweep would fail on correct JSON and teach the next reader to loosen it.
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var link = body.GetProperty("assetLinks")[0];

        Assert.Equal(
            (string[])["id", "sortOrder", "mediaAssetId", "role", "caption"],
            link.EnumerateObject().Select(property => property.Name));
    }

    [Fact]
    public async Task Timestamps_are_published_in_utc()
    {
        var recipeId = await SeedFullRecipeAsync(_fixture.WorkspaceA);

        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: TestContext.Current.CancellationToken);
        var response = await client.GetAsync(RecipeIn(_fixture.WorkspaceA, recipeId), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        // api-contract.md requires ISO 8601 UTC. The chain that makes this true — IClock.UtcNow through a
        // datetimeoffset column — is right today and has nothing else asserting it stays that way.
        foreach (var field in (string[])["createdAt", "updatedAt"])
        {
            Assert.Equal(
                TimeSpan.Zero,
                DateTimeOffset.Parse(body.GetProperty(field).GetString()!, CultureInfo.InvariantCulture).Offset);
        }
    }

    [Fact]
    public async Task The_concurrency_token_is_the_recipes_row_version_and_is_stable_while_it_is_unchanged()
    {
        var recipeId = await SeedFullRecipeAsync(_fixture.WorkspaceA);

        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: TestContext.Current.CancellationToken);
        var first = await ReadTokenAsync(client, recipeId);
        var second = await ReadTokenAsync(client, recipeId);

        // Stability across reads of an unchanged recipe is the property the update seam will depend on: a token
        // that varied per read would reject every edit as stale.
        Assert.Equal(first, second);

        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        var stored = await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .Recipes.IgnoreQueryFilters()
            .Where(recipe => recipe.Id == recipeId)
            .Select(recipe => recipe.RowVersion)
            .SingleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(Convert.ToBase64String(stored), first);
    }

    private async Task<string> ReadTokenAsync(GatewayClient client, Guid recipeId)
    {
        var response = await client.GetAsync(RecipeIn(_fixture.WorkspaceA, recipeId), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        return body.GetProperty("concurrencyToken").GetString()!;
    }

    [Fact]
    public async Task Each_workspace_reads_its_own_tag_of_the_same_name()
    {
        using var ownerA = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: TestContext.Current.CancellationToken);
        using var ownerB = await _fixture.SignInAsync(_fixture.WorkspaceB.OwnerEmail, cancellationToken: TestContext.Current.CancellationToken);

        // The same tag word in both workspaces, so only ownership distinguishes the two vocabulary rows. This
        // is the case that ties the detail read's second query — the tag lookup by id — to a real request.
        var inA = await CreateTaggedAsync(ownerA, _fixture.WorkspaceA);
        var inB = await CreateTaggedAsync(ownerB, _fixture.WorkspaceB);

        var fromA = await ReadTagAsync(ownerA, _fixture.WorkspaceA, inA);
        var fromB = await ReadTagAsync(ownerB, _fixture.WorkspaceB, inB);

        Assert.Equal("weeknight", fromA.GetProperty("name").GetString());
        Assert.Equal("weeknight", fromB.GetProperty("name").GetString());
        Assert.NotEqual(
            fromA.GetProperty("workspaceTagId").GetGuid(),
            fromB.GetProperty("workspaceTagId").GetGuid());
    }

    private async Task<Guid> CreateTaggedAsync(GatewayClient client, SeededWorkspace workspace)
    {
        var created = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspace.Slug}/recipes",
            new { title = "Olive oil cake", tags = new[] { "weeknight" } },
            TestContext.Current.CancellationToken);

        return (await created.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken))
            .GetProperty("recipeId").GetGuid();
    }

    private async Task<JsonElement> ReadTagAsync(GatewayClient client, SeededWorkspace workspace, Guid recipeId)
    {
        var response = await client.GetAsync(RecipeIn(workspace, recipeId), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        return body.GetProperty("tags").EnumerateArray().Single();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Every_member_of_the_workspace_may_read_its_recipes(bool asOwner)
    {
        // Workspace B's second member is a Viewer — the lowest role there is, and the one this must admit.
        var recipeId = await SeedFullRecipeAsync(_fixture.WorkspaceB);
        var email = asOwner ? _fixture.WorkspaceB.OwnerEmail : _fixture.WorkspaceB.MemberEmail;

        using var client = await _fixture.SignInAsync(email, cancellationToken: TestContext.Current.CancellationToken);
        var response = await client.GetAsync(RecipeIn(_fixture.WorkspaceB, recipeId), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---- What cannot be read ----

    [Fact]
    public async Task An_unknown_recipe_is_not_found()
    {
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: TestContext.Current.CancellationToken);

        var response = await client.GetAsync(RecipeIn(_fixture.WorkspaceA, Guid.NewGuid()), TestContext.Current.CancellationToken);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(RecipeErrorCodes.RecipeNotFound, problem.GetProperty("code").GetString());
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task A_malformed_recipe_id_answers_with_the_same_status()
    {
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: TestContext.Current.CancellationToken);

        var response = await client.GetAsync(
            $"/api/v1/workspaces/{_fixture.WorkspaceA.Slug}/recipes/not-a-guid", TestContext.Current.CancellationToken);

        // From the route constraint rather than from the facade, so the status matches while the problem body
        // carries the edge's generic code. Pinned deliberately: the status is what would disclose something,
        // and it does not, but the code difference is real and a client branching on it should not be
        // surprised by it later.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("not_found", problem.GetProperty("code").GetString());
    }

    // ---- Two workspaces ----

    [Fact]
    public async Task A_recipe_of_one_workspace_is_not_found_through_the_others_route()
    {
        var recipeId = await SeedFullRecipeAsync(_fixture.WorkspaceA);

        using var client = await _fixture.SignInAsync(_fixture.WorkspaceB.OwnerEmail, cancellationToken: TestContext.Current.CancellationToken);
        var response = await client.GetAsync(RecipeIn(_fixture.WorkspaceB, recipeId), TestContext.Current.CancellationToken);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        // B's owner names A's recipe id under B's slug. Indistinguishable from an id that never existed —
        // including the error code, because a different code would itself be the disclosure.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(RecipeErrorCodes.RecipeNotFound, problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_non_member_naming_the_owning_workspace_is_not_found_either()
    {
        var recipeId = await SeedFullRecipeAsync(_fixture.WorkspaceA);

        using var client = await _fixture.SignInAsync(_fixture.WorkspaceB.OwnerEmail, cancellationToken: TestContext.Current.CancellationToken);
        var response = await client.GetAsync(RecipeIn(_fixture.WorkspaceA, recipeId), TestContext.Current.CancellationToken);

        // The correct slug and the correct recipe id, from someone who belongs to neither: 404, not 403, so the
        // reply does not confirm that Workspace A exists (tenancy.md).
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Two_workspaces_read_their_own_recipe_of_the_same_name()
    {
        var inA = await SeedFullRecipeAsync(_fixture.WorkspaceA, "Shared title");
        var inB = await SeedFullRecipeAsync(_fixture.WorkspaceB, "Shared title");

        using var ownerA = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: TestContext.Current.CancellationToken);
        using var ownerB = await _fixture.SignInAsync(_fixture.WorkspaceB.OwnerEmail, cancellationToken: TestContext.Current.CancellationToken);

        var fromA = await ownerA.GetAsync(RecipeIn(_fixture.WorkspaceA, inA), TestContext.Current.CancellationToken);
        var fromB = await ownerB.GetAsync(RecipeIn(_fixture.WorkspaceB, inB), TestContext.Current.CancellationToken);

        // Same title in both, so only ownership distinguishes them — and each reader gets their own.
        Assert.Equal(inA, (await fromA.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("id").GetGuid());
        Assert.Equal(inB, (await fromB.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("id").GetGuid());
    }

    /// <summary>
    /// Writes a recipe with one of every child directly, because the create contract carries no ingredients,
    /// steps, equipment or media yet.
    /// </summary>
    /// <remarks>
    /// Seeding through the DbContext rather than through HTTP is the only way to test that structured content
    /// survives the read while the write seam for it does not exist. The workspace is resolved in this scope
    /// exactly as a request would resolve it, so the ownership interceptor stamps these rows rather than the
    /// test asserting ownership it assigned itself.
    /// </remarks>
    private async Task<Guid> SeedFullRecipeAsync(SeededWorkspace workspace, string title = "Olive oil cake")
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();

        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspace.Id, workspace.Slug, Guid.NewGuid(), WorkspaceRole.Owner);

        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var recipe = SeededRecipe(title);

        db.Recipes.Add(recipe);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return recipe.Id;
    }

    /// <summary>
    /// A recipe with two of each ordered child, seeded with their sort orders reversed so that a response in
    /// insertion order is distinguishable from one in sort order.
    /// </summary>
    private static Recipe SeededRecipe(string title)
    {
        var recipe = new Recipe
        {
            Id = Guid.NewGuid(),
            Title = title,
            Status = RecipeStatus.Draft,
            CreatedByMembershipId = Guid.NewGuid(),
            UpdatedByMembershipId = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        foreach (var groupOrder in (int[])[1, 0])
        {
            var ingredients = new RecipeIngredientGroup
            {
                Id = Guid.NewGuid(),
                RecipeId = recipe.Id,
                Title = $"Group {groupOrder}",
                SortOrder = groupOrder,
            };

            foreach (var lineOrder in (int[])[2, 0, 1])
            {
                ingredients.Ingredients.Add(new RecipeIngredient
                {
                    Id = Guid.NewGuid(),
                    RecipeId = recipe.Id,
                    RecipeIngredientGroupId = ingredients.Id,
                    SortOrder = lineOrder,
                    DisplayText = $"2 cups (240 g) flour — line {groupOrder}.{lineOrder}",
                    Quantity = 240m,

                    // Not Matched: CK_RecipeIngredients_Match_Status requires a matched line to name the
                    // ingredient it matched, and this fixture seeds no platform reference data.
                    MatchStatus = IngredientMatchStatus.NoMatch,
                });
            }

            recipe.IngredientGroups.Add(ingredients);

            var steps = new RecipeInstructionGroup
            {
                Id = Guid.NewGuid(),
                RecipeId = recipe.Id,
                Title = $"Steps {groupOrder}",
                SortOrder = groupOrder,
            };

            steps.Steps.Add(new RecipeInstructionStep
            {
                Id = Guid.NewGuid(),
                RecipeId = recipe.Id,
                RecipeInstructionGroupId = steps.Id,
                SortOrder = 0,
                Text = $"Bake until done — step {groupOrder}.",
            });

            recipe.InstructionGroups.Add(steps);

            recipe.Equipment.Add(new RecipeEquipment
            {
                Id = Guid.NewGuid(),
                RecipeId = recipe.Id,
                SortOrder = groupOrder,
                DisplayText = $"9-inch tin {groupOrder}",
            });

            recipe.AssetLinks.Add(new RecipeAssetLink
            {
                Id = Guid.NewGuid(),
                RecipeId = recipe.Id,
                SortOrder = groupOrder,
                MediaAssetId = Guid.NewGuid(),
                Role = groupOrder == 0 ? RecipeAssetRole.Hero : RecipeAssetRole.Gallery,
            });
        }

        return recipe;
    }
}

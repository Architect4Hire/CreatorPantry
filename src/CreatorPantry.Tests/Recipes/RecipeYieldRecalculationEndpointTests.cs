using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using CreatorPantry.Tests.Gateway;
using CreatorPantry.Tests.Tenancy;

namespace CreatorPantry.Tests.Recipes;

/// <summary>
/// <c>POST /api/v1/workspaces/{workspaceSlug}/recipes/{recipeId}/calculations/recalculate-yield</c> through
/// the real Gateway — real cookie session, real gateway-signed internal token, real API — for the complete,
/// partial, and contradictory cases ING-005 defines, and what one workspace can learn about the other's
/// recipes (ING-005, 7.11c).
/// </summary>
public sealed class RecipeYieldRecalculationEndpointTests : IAsyncLifetime
{
    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync() => _fixture = await TwoWorkspaceGatewayFixture.CreateAsync();

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static string RecalculateYieldOf(SeededWorkspace workspace, Guid recipeId) =>
        $"/api/v1/workspaces/{workspace.Slug}/recipes/{recipeId}/calculations/recalculate-yield";

    // ---- Complete (all three known) ----

    [Fact]
    public async Task Three_agreeing_values_reconcile()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var recipeId = await CreateAsync(client, _fixture.WorkspaceA);

        var body = await ReadAsync(await client.PostAsJsonAsync(
            RecalculateYieldOf(_fixture.WorkspaceA, recipeId),
            new { sourceVersionNumber = 1, batchYield = 12m, servingCount = 6m, servingSize = 2m },
            TestContext.Current.CancellationToken));

        Assert.Equal(1, body.GetProperty("sourceVersionNumber").GetInt32());

        var preview = body.GetProperty("preview");
        Assert.Equal("Reconciled", preview.GetProperty("status").GetString());
        Assert.Equal(12m, preview.GetProperty("batchYield").GetDecimal());
    }

    [Fact]
    public async Task Three_disagreeing_values_are_reported_without_overriding_any_of_them()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var recipeId = await CreateAsync(client, _fixture.WorkspaceA);

        var body = await ReadAsync(await client.PostAsJsonAsync(
            RecalculateYieldOf(_fixture.WorkspaceA, recipeId),
            new { sourceVersionNumber = 1, batchYield = 99m, servingCount = 6m, servingSize = 2m },
            TestContext.Current.CancellationToken));

        var preview = body.GetProperty("preview");
        Assert.Equal("Contradictory", preview.GetProperty("status").GetString());
        Assert.Equal(99m, preview.GetProperty("batchYield").GetDecimal());
        Assert.Equal(6m, preview.GetProperty("servingCount").GetDecimal());
        Assert.Equal(2m, preview.GetProperty("servingSize").GetDecimal());
    }

    // ---- Partial (two known) ----

    [Fact]
    public async Task Two_known_values_solve_the_third()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var recipeId = await CreateAsync(client, _fixture.WorkspaceA);

        var body = await ReadAsync(await client.PostAsJsonAsync(
            RecalculateYieldOf(_fixture.WorkspaceA, recipeId),
            new { sourceVersionNumber = 1, servingCount = 6m, servingSize = 2m },
            TestContext.Current.CancellationToken));

        var preview = body.GetProperty("preview");
        Assert.Equal("Solved", preview.GetProperty("status").GetString());
        Assert.Equal("BatchYield", preview.GetProperty("solvedField").GetString());
        Assert.Equal(12m, preview.GetProperty("batchYield").GetDecimal());
    }

    /// <summary>recipes.md/ING-005: fewer than two known values is answered, never invented.</summary>
    [Fact]
    public async Task Fewer_than_two_values_is_insufficient_input()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var recipeId = await CreateAsync(client, _fixture.WorkspaceA);

        var body = await ReadAsync(await client.PostAsJsonAsync(
            RecalculateYieldOf(_fixture.WorkspaceA, recipeId),
            new { sourceVersionNumber = 1, batchYield = 12m },
            TestContext.Current.CancellationToken));

        var preview = body.GetProperty("preview");
        Assert.Equal("InsufficientInput", preview.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, preview.GetProperty("formula").ValueKind);
    }

    // ---- Refusals ----

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    public async Task A_non_positive_batch_yield_is_refused(decimal batchYield)
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var recipeId = await CreateAsync(client, _fixture.WorkspaceA);

        var response = await client.PostAsJsonAsync(
            RecalculateYieldOf(_fixture.WorkspaceA, recipeId),
            new { sourceVersionNumber = 1, batchYield, servingCount = 6m, servingSize = 2m },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await ReadAsync(response);
        Assert.Equal(RecipeErrorCodes.YieldRecalculationInvalidRequest, body.GetProperty("code").GetString());
        Assert.True(body.GetProperty("errors").TryGetProperty("batchYield", out _));
    }

    [Fact]
    public async Task A_source_version_below_one_is_refused_before_the_recipe_is_read()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);

        var response = await client.PostAsJsonAsync(
            RecalculateYieldOf(_fixture.WorkspaceA, Guid.NewGuid()),
            new { sourceVersionNumber = 0, batchYield = 12m, servingCount = 6m, servingSize = 2m },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            RecipeErrorCodes.YieldRecalculationInvalidRequest,
            (await ReadAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_version_number_the_recipe_does_not_have_names_the_parameter_at_fault()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var recipeId = await CreateAsync(client, _fixture.WorkspaceA);

        var response = await client.PostAsJsonAsync(
            RecalculateYieldOf(_fixture.WorkspaceA, recipeId),
            new { sourceVersionNumber = 9, batchYield = 12m, servingCount = 6m, servingSize = 2m },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var body = await ReadAsync(response);
        Assert.Equal(RecipeErrorCodes.VersionNotFound, body.GetProperty("code").GetString());
        Assert.True(body.GetProperty("errors").TryGetProperty("sourceVersionNumber", out _));
    }

    // ---- Authorization ----

    [Fact]
    public async Task Every_member_including_a_viewer_may_recalculate_yield()
    {
        using var owner = await SignInAsync(_fixture.WorkspaceB);
        var recipeId = await CreateAsync(owner, _fixture.WorkspaceB);

        using var member = await SignInAsync(_fixture.WorkspaceB, asOwner: false);

        var response = await member.PostAsJsonAsync(
            RecalculateYieldOf(_fixture.WorkspaceB, recipeId),
            new { sourceVersionNumber = 1, batchYield = 12m, servingCount = 6m, servingSize = 2m },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---- Isolation ----

    [Fact]
    public async Task An_unknown_recipe_and_another_workspaces_recipe_answer_identically()
    {
        using var ownerB = await SignInAsync(_fixture.WorkspaceB);
        var recipeInB = await CreateAsync(ownerB, _fixture.WorkspaceB);

        using var ownerA = await SignInAsync(_fixture.WorkspaceA);
        var token = TestContext.Current.CancellationToken;
        var request = new { sourceVersionNumber = 1, batchYield = 12m, servingCount = 6m, servingSize = 2m };

        var unknown = await ownerA.PostAsJsonAsync(RecalculateYieldOf(_fixture.WorkspaceA, Guid.NewGuid()), request, token);
        var othersRecipe = await ownerA.PostAsJsonAsync(RecalculateYieldOf(_fixture.WorkspaceA, recipeInB), request, token);

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, othersRecipe.StatusCode);

        var unknownBody = await ReadAsync(unknown);
        var othersBody = await ReadAsync(othersRecipe);

        Assert.Equal(RecipeErrorCodes.RecipeNotFound, unknownBody.GetProperty("code").GetString());
        Assert.Equal(WithoutTrace(unknownBody), WithoutTrace(othersBody));
    }

    [Fact]
    public async Task A_member_of_one_workspace_cannot_recalculate_yield_through_the_other_slug()
    {
        using var ownerB = await SignInAsync(_fixture.WorkspaceB);
        var recipeInB = await CreateAsync(ownerB, _fixture.WorkspaceB);

        using var ownerA = await SignInAsync(_fixture.WorkspaceA);

        var response = await ownerA.PostAsJsonAsync(
            RecalculateYieldOf(_fixture.WorkspaceB, recipeInB),
            new { sourceVersionNumber = 1, batchYield = 12m, servingCount = 6m, servingSize = 2m },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- Helpers ----

    private Task<GatewayClient> SignInAsync(SeededWorkspace workspace, bool asOwner = true) =>
        _fixture.SignInAsync(
            asOwner ? workspace.OwnerEmail : workspace.MemberEmail,
            cancellationToken: TestContext.Current.CancellationToken);

    private static async Task<Guid> CreateAsync(
        GatewayClient client, SeededWorkspace workspace, string title = "Olive oil cake")
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspace.Slug}/recipes",
            new { title },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await ReadAsync(response)).GetProperty("recipeId").GetGuid();
    }

    /// <summary>
    /// A problem body with its trace id removed, so two refusals can be compared for being the same refusal
    /// rather than for having happened in the same request.
    /// </summary>
    private static string WithoutTrace(JsonElement body) =>
        JsonSerializer.Serialize(body.EnumerateObject()
            .Where(property => !property.Name.Contains("traceId", StringComparison.OrdinalIgnoreCase)
                && !property.Name.Contains("correlation", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(property => property.Name, property => property.Value.ToString()));

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
}

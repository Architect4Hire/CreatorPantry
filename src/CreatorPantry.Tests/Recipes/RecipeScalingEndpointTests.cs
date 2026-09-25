using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using CreatorPantry.Tests.Gateway;
using CreatorPantry.Tests.Tenancy;

namespace CreatorPantry.Tests.Recipes;

/// <summary>
/// <c>POST /api/v1/workspaces/{workspaceSlug}/recipes/{recipeId}/calculations/scale</c> through the real
/// Gateway — real cookie session, real gateway-signed internal token, real API — for source/provenance, the
/// requests it refuses, and what one workspace can learn about the other's recipes (ING-003, 7.11a).
/// </summary>
public sealed class RecipeScalingEndpointTests : IAsyncLifetime
{
    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync() => _fixture = await TwoWorkspaceGatewayFixture.CreateAsync();

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static string ScaleOf(SeededWorkspace workspace, Guid recipeId) =>
        $"/api/v1/workspaces/{workspace.Slug}/recipes/{recipeId}/calculations/scale";

    // ---- Source and provenance ----

    [Fact]
    public async Task A_multiplier_scale_reports_the_source_version_and_the_resolved_factor()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var recipeId = await CreateAsync(client, _fixture.WorkspaceA);

        var body = await ReadAsync(await client.PostAsJsonAsync(
            ScaleOf(_fixture.WorkspaceA, recipeId),
            new { sourceVersionNumber = 1, multiplier = 2m },
            TestContext.Current.CancellationToken));

        Assert.Equal(1, body.GetProperty("sourceVersionNumber").GetInt32());

        var factor = body.GetProperty("preview").GetProperty("factor");
        Assert.Equal("2", factor.GetProperty("numerator").GetString());
        Assert.Equal("1", factor.GetProperty("denominator").GetString());

        // A title-only recipe has no ingredient lines to scale and no structured yield to scale toward.
        Assert.Empty(body.GetProperty("preview").GetProperty("lines").EnumerateArray());
        Assert.Contains(
            "RecipeYieldNotStructured",
            body.GetProperty("preview").GetProperty("recipeWarnings").EnumerateArray().Select(w => w.GetString()));
    }

    [Fact]
    public async Task A_second_version_is_named_as_the_source_when_asked_for()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var recipeId = await CreateAsync(client, _fixture.WorkspaceA);
        await EditAsync(client, _fixture.WorkspaceA, recipeId, "Olive oil and rosemary cake");

        var body = await ReadAsync(await client.PostAsJsonAsync(
            ScaleOf(_fixture.WorkspaceA, recipeId),
            new { sourceVersionNumber = 2, multiplier = 1.5m },
            TestContext.Current.CancellationToken));

        Assert.Equal(2, body.GetProperty("sourceVersionNumber").GetInt32());
    }

    /// <summary>
    /// Read-only, exactly as the RESTRICTION requires: the recipe's own current version is unaffected by
    /// having been scaled, however many times.
    /// </summary>
    [Fact]
    public async Task Scaling_does_not_change_the_recipes_current_version()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var recipeId = await CreateAsync(client, _fixture.WorkspaceA);
        var token = TestContext.Current.CancellationToken;

        await client.PostAsJsonAsync(
            ScaleOf(_fixture.WorkspaceA, recipeId), new { sourceVersionNumber = 1, multiplier = 3m }, token);
        await client.PostAsJsonAsync(
            ScaleOf(_fixture.WorkspaceA, recipeId), new { sourceVersionNumber = 1, multiplier = 5m }, token);

        var detail = await ReadAsync(await client.GetAsync(
            $"/api/v1/workspaces/{_fixture.WorkspaceA.Slug}/recipes/{recipeId}", token));

        Assert.Equal(1, detail.GetProperty("currentVersion").GetProperty("versionNumber").GetInt32());
    }

    // ---- Refusals ----

    [Fact]
    public async Task Neither_multiplier_nor_target_yield_is_refused()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var recipeId = await CreateAsync(client, _fixture.WorkspaceA);

        var response = await client.PostAsJsonAsync(
            ScaleOf(_fixture.WorkspaceA, recipeId),
            new { sourceVersionNumber = 1 },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            RecipeErrorCodes.ScalingInvalidRequest,
            (await ReadAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Both_multiplier_and_target_yield_is_refused()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var recipeId = await CreateAsync(client, _fixture.WorkspaceA);

        var response = await client.PostAsJsonAsync(
            ScaleOf(_fixture.WorkspaceA, recipeId),
            new { sourceVersionNumber = 1, multiplier = 2m, targetYieldQuantity = 4m },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            RecipeErrorCodes.ScalingInvalidRequest,
            (await ReadAsync(response)).GetProperty("code").GetString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    public async Task A_non_positive_multiplier_is_refused(decimal multiplier)
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var recipeId = await CreateAsync(client, _fixture.WorkspaceA);

        var response = await client.PostAsJsonAsync(
            ScaleOf(_fixture.WorkspaceA, recipeId),
            new { sourceVersionNumber = 1, multiplier },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_source_version_below_one_is_refused_before_the_recipe_is_read()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);

        var response = await client.PostAsJsonAsync(
            ScaleOf(_fixture.WorkspaceA, Guid.NewGuid()),
            new { sourceVersionNumber = 0, multiplier = 2m },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            RecipeErrorCodes.ScalingInvalidRequest,
            (await ReadAsync(response)).GetProperty("code").GetString());
    }

    /// <summary>
    /// A target yield needs a structured number to scale toward, and recipes.md forbids inventing a missing
    /// serving definition. This recipe was never given one.
    /// </summary>
    [Fact]
    public async Task A_target_yield_against_a_recipe_with_no_structured_yield_is_refused()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var recipeId = await CreateAsync(client, _fixture.WorkspaceA);

        var response = await client.PostAsJsonAsync(
            ScaleOf(_fixture.WorkspaceA, recipeId),
            new { sourceVersionNumber = 1, targetYieldQuantity = 24m },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            RecipeErrorCodes.ScalingInvalidRequest,
            (await ReadAsync(response)).GetProperty("code").GetString());
    }

    /// <summary>
    /// A version number this recipe does not have is its own refusal, naming the parameter at fault — the
    /// same reasoning as the version comparison route.
    /// </summary>
    [Fact]
    public async Task A_version_number_the_recipe_does_not_have_names_the_parameter_at_fault()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var recipeId = await CreateAsync(client, _fixture.WorkspaceA);

        var response = await client.PostAsJsonAsync(
            ScaleOf(_fixture.WorkspaceA, recipeId),
            new { sourceVersionNumber = 9, multiplier = 2m },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var body = await ReadAsync(response);
        Assert.Equal(RecipeErrorCodes.VersionNotFound, body.GetProperty("code").GetString());
        Assert.True(body.GetProperty("errors").TryGetProperty("sourceVersionNumber", out _));
    }

    // ---- Authorization ----

    [Fact]
    public async Task Every_member_including_a_viewer_may_scale()
    {
        using var owner = await SignInAsync(_fixture.WorkspaceB);
        var recipeId = await CreateAsync(owner, _fixture.WorkspaceB);

        using var member = await SignInAsync(_fixture.WorkspaceB, asOwner: false);

        var response = await member.PostAsJsonAsync(
            ScaleOf(_fixture.WorkspaceB, recipeId),
            new { sourceVersionNumber = 1, multiplier = 2m },
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
        var request = new { sourceVersionNumber = 1, multiplier = 2m };

        var unknown = await ownerA.PostAsJsonAsync(ScaleOf(_fixture.WorkspaceA, Guid.NewGuid()), request, token);
        var othersRecipe = await ownerA.PostAsJsonAsync(ScaleOf(_fixture.WorkspaceA, recipeInB), request, token);

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, othersRecipe.StatusCode);

        var unknownBody = await ReadAsync(unknown);
        var othersBody = await ReadAsync(othersRecipe);

        Assert.Equal(RecipeErrorCodes.RecipeNotFound, unknownBody.GetProperty("code").GetString());
        Assert.Equal(WithoutTrace(unknownBody), WithoutTrace(othersBody));
    }

    [Fact]
    public async Task A_member_of_one_workspace_cannot_scale_through_the_other_slug()
    {
        using var ownerB = await SignInAsync(_fixture.WorkspaceB);
        var recipeInB = await CreateAsync(ownerB, _fixture.WorkspaceB);

        using var ownerA = await SignInAsync(_fixture.WorkspaceA);

        var response = await ownerA.PostAsJsonAsync(
            ScaleOf(_fixture.WorkspaceB, recipeInB),
            new { sourceVersionNumber = 1, multiplier = 2m },
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

    private static async Task EditAsync(GatewayClient client, SeededWorkspace workspace, Guid recipeId, string title)
    {
        var token = TestContext.Current.CancellationToken;
        var route = $"/api/v1/workspaces/{workspace.Slug}/recipes/{recipeId}";

        var detail = await ReadAsync(await client.GetAsync(route, token));

        var response = await client.PatchAsJsonAsync(
            route,
            new { expectedConcurrencyToken = detail.GetProperty("concurrencyToken").GetString(), title },
            token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
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

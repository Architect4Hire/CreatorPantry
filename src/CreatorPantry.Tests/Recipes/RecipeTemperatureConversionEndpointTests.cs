using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using CreatorPantry.Tests.Gateway;
using CreatorPantry.Tests.Tenancy;

namespace CreatorPantry.Tests.Recipes;

/// <summary>
/// <c>POST /api/v1/workspaces/{workspaceSlug}/recipes/{recipeId}/calculations/convert-temperature</c> through
/// the real Gateway — real cookie session, real gateway-signed internal token, real API — for validation, the
/// pass-through context CALC-003 requires, and what one workspace can learn about the other's recipes
/// (CALC-003, 7.11b).
/// </summary>
public sealed class RecipeTemperatureConversionEndpointTests : IAsyncLifetime
{
    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync() => _fixture = await TwoWorkspaceGatewayFixture.CreateAsync();

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static string ConvertTemperatureOf(SeededWorkspace workspace, Guid recipeId) =>
        $"/api/v1/workspaces/{workspace.Slug}/recipes/{recipeId}/calculations/convert-temperature";

    // ---- Validation ----

    [Fact]
    public async Task A_source_version_below_one_is_refused_before_the_recipe_is_read()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);

        var response = await client.PostAsJsonAsync(
            ConvertTemperatureOf(_fixture.WorkspaceA, Guid.NewGuid()),
            new { sourceVersionNumber = 0, value = 180m, fromScale = "Celsius", toScale = "Fahrenheit", precision = 0 },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            RecipeErrorCodes.TemperatureConversionInvalidRequest,
            (await ReadAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_negative_precision_is_refused()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var recipeId = await CreateAsync(client, _fixture.WorkspaceA);

        var response = await client.PostAsJsonAsync(
            ConvertTemperatureOf(_fixture.WorkspaceA, recipeId),
            new { sourceVersionNumber = 1, value = 180m, fromScale = "Celsius", toScale = "Fahrenheit", precision = -1 },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            RecipeErrorCodes.TemperatureConversionInvalidRequest,
            (await ReadAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task An_unrecognized_scale_is_refused()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var recipeId = await CreateAsync(client, _fixture.WorkspaceA);

        var response = await client.PostAsJsonAsync(
            ConvertTemperatureOf(_fixture.WorkspaceA, recipeId),
            new { sourceVersionNumber = 1, value = 180m, fromScale = "Kelvin", toScale = "Fahrenheit", precision = 0 },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_version_number_the_recipe_does_not_have_names_the_parameter_at_fault()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var recipeId = await CreateAsync(client, _fixture.WorkspaceA);

        var response = await client.PostAsJsonAsync(
            ConvertTemperatureOf(_fixture.WorkspaceA, recipeId),
            new { sourceVersionNumber = 9, value = 180m, fromScale = "Celsius", toScale = "Fahrenheit", precision = 0 },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var body = await ReadAsync(response);
        Assert.Equal(RecipeErrorCodes.VersionNotFound, body.GetProperty("code").GetString());
        Assert.True(body.GetProperty("errors").TryGetProperty("sourceVersionNumber", out _));
    }

    // ---- Context ----

    [Fact]
    public async Task A_structured_temperature_converts_and_echoes_oven_mode_and_safety_note_unchanged()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var recipeId = await CreateAsync(client, _fixture.WorkspaceA);

        var body = await ReadAsync(await client.PostAsJsonAsync(
            ConvertTemperatureOf(_fixture.WorkspaceA, recipeId),
            new
            {
                sourceVersionNumber = 1,
                value = 180m,
                fromScale = "Celsius",
                toScale = "Fahrenheit",
                precision = 0,
                ovenModeContext = "convection",
                safetyNote = "until golden and a skewer comes out clean",
            },
            TestContext.Current.CancellationToken));

        Assert.Equal(1, body.GetProperty("sourceVersionNumber").GetInt32());

        var result = body.GetProperty("result");
        Assert.Equal(356m, result.GetProperty("convertedDisplayValue").GetDecimal());
        Assert.Equal("Fahrenheit", result.GetProperty("convertedScale").GetString());
        Assert.Equal("convection", result.GetProperty("ovenModeContext").GetString());
        Assert.Equal("until golden and a skewer comes out clean", result.GetProperty("safetyNote").GetString());
    }

    [Fact]
    public async Task Absent_context_fields_stay_absent_rather_than_being_invented()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var recipeId = await CreateAsync(client, _fixture.WorkspaceA);

        var body = await ReadAsync(await client.PostAsJsonAsync(
            ConvertTemperatureOf(_fixture.WorkspaceA, recipeId),
            new { sourceVersionNumber = 1, value = 180m, fromScale = "Celsius", toScale = "Fahrenheit", precision = 0 },
            TestContext.Current.CancellationToken));

        var result = body.GetProperty("result");
        Assert.Equal(JsonValueKind.Null, result.GetProperty("ovenModeContext").ValueKind);
        Assert.Equal(JsonValueKind.Null, result.GetProperty("safetyNote").ValueKind);
    }

    [Fact]
    public async Task The_same_source_and_target_scale_is_an_identity_conversion()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var recipeId = await CreateAsync(client, _fixture.WorkspaceA);

        var body = await ReadAsync(await client.PostAsJsonAsync(
            ConvertTemperatureOf(_fixture.WorkspaceA, recipeId),
            new { sourceVersionNumber = 1, value = 74m, fromScale = "Celsius", toScale = "Celsius", precision = 0 },
            TestContext.Current.CancellationToken));

        Assert.Equal(74m, body.GetProperty("result").GetProperty("convertedDisplayValue").GetDecimal());
    }

    // ---- Authorization ----

    [Fact]
    public async Task Every_member_including_a_viewer_may_convert_temperature()
    {
        using var owner = await SignInAsync(_fixture.WorkspaceB);
        var recipeId = await CreateAsync(owner, _fixture.WorkspaceB);

        using var member = await SignInAsync(_fixture.WorkspaceB, asOwner: false);

        var response = await member.PostAsJsonAsync(
            ConvertTemperatureOf(_fixture.WorkspaceB, recipeId),
            new { sourceVersionNumber = 1, value = 180m, fromScale = "Celsius", toScale = "Fahrenheit", precision = 0 },
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
        var request = new { sourceVersionNumber = 1, value = 180m, fromScale = "Celsius", toScale = "Fahrenheit", precision = 0 };

        var unknown = await ownerA.PostAsJsonAsync(ConvertTemperatureOf(_fixture.WorkspaceA, Guid.NewGuid()), request, token);
        var othersRecipe = await ownerA.PostAsJsonAsync(ConvertTemperatureOf(_fixture.WorkspaceA, recipeInB), request, token);

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, othersRecipe.StatusCode);

        var unknownBody = await ReadAsync(unknown);
        var othersBody = await ReadAsync(othersRecipe);

        Assert.Equal(RecipeErrorCodes.RecipeNotFound, unknownBody.GetProperty("code").GetString());
        Assert.Equal(WithoutTrace(unknownBody), WithoutTrace(othersBody));
    }

    [Fact]
    public async Task A_member_of_one_workspace_cannot_convert_temperature_through_the_other_slug()
    {
        using var ownerB = await SignInAsync(_fixture.WorkspaceB);
        var recipeInB = await CreateAsync(ownerB, _fixture.WorkspaceB);

        using var ownerA = await SignInAsync(_fixture.WorkspaceA);

        var response = await ownerA.PostAsJsonAsync(
            ConvertTemperatureOf(_fixture.WorkspaceB, recipeInB),
            new { sourceVersionNumber = 1, value = 180m, fromScale = "Celsius", toScale = "Fahrenheit", precision = 0 },
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

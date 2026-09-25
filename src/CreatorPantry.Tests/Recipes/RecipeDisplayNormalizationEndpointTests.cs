using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Modules.Measurement.Data.Entities;
using CreatorPantry.Domain.Modules.Measurement.Managers;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using CreatorPantry.Tests.Gateway;
using CreatorPantry.Tests.Tenancy;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Recipes;

/// <summary>
/// <c>POST /api/v1/workspaces/{workspaceSlug}/recipes/{recipeId}/calculations/normalize-display</c> through
/// the real Gateway — real cookie session, real gateway-signed internal token, real API — for rounding, range
/// preservation, unit-system presentation, and what one workspace can learn about the other's recipes
/// (ING-006, 7.11d).
/// </summary>
public sealed class RecipeDisplayNormalizationEndpointTests : IAsyncLifetime
{
    private TwoWorkspaceGatewayFixture _fixture = null!;
    private Guid _cupUsId;
    private Guid _gramId;

    public async ValueTask InitializeAsync()
    {
        _fixture = await TwoWorkspaceGatewayFixture.CreateAsync();

        _cupUsId = await SeedUnitAsync(
            "cup-us", "cup", "cups", "c", MeasurementDimension.Volume, MeasurementSystem.UsCustomary, 236.5882365m, 2);
        _gramId = await SeedUnitAsync("g", "gram", "grams", "g", MeasurementDimension.Mass, MeasurementSystem.Metric, 1m, 0);
    }

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static string NormalizeDisplayOf(SeededWorkspace workspace, Guid recipeId) =>
        $"/api/v1/workspaces/{workspace.Slug}/recipes/{recipeId}/calculations/normalize-display";

    // ---- Rounding ----

    [Fact]
    public async Task ToEven_rounds_a_midpoint_toward_the_even_digit()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var recipeId = await CreateAsync(client, _fixture.WorkspaceA);

        var body = await ReadAsync(await client.PostAsJsonAsync(
            NormalizeDisplayOf(_fixture.WorkspaceA, recipeId),
            new { sourceVersionNumber = 1, value = 1.025m, unitId = _cupUsId, precision = 2, useAbbreviation = false, rounding = "ToEven" },
            TestContext.Current.CancellationToken));

        Assert.Equal(1, body.GetProperty("sourceVersionNumber").GetInt32());
        Assert.Equal("1.02 cups", body.GetProperty("result").GetProperty("text").GetString());
    }

    [Fact]
    public async Task AwayFromZero_rounds_the_same_midpoint_up()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var recipeId = await CreateAsync(client, _fixture.WorkspaceA);

        var body = await ReadAsync(await client.PostAsJsonAsync(
            NormalizeDisplayOf(_fixture.WorkspaceA, recipeId),
            new { sourceVersionNumber = 1, value = 1.025m, unitId = _cupUsId, precision = 2, useAbbreviation = false, rounding = "AwayFromZero" },
            TestContext.Current.CancellationToken));

        Assert.Equal("1.03 cups", body.GetProperty("result").GetProperty("text").GetString());
    }

    [Fact]
    public async Task A_common_kitchen_fraction_renders_a_glyph_instead_of_a_decimal()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var recipeId = await CreateAsync(client, _fixture.WorkspaceA);

        var body = await ReadAsync(await client.PostAsJsonAsync(
            NormalizeDisplayOf(_fixture.WorkspaceA, recipeId),
            new { sourceVersionNumber = 1, value = 1.5m, unitId = _cupUsId, precision = 2, useAbbreviation = false, rounding = "ToEven" },
            TestContext.Current.CancellationToken));

        var result = body.GetProperty("result");
        Assert.Equal("1½ cups", result.GetProperty("text").GetString());
        Assert.Equal("Fraction", result.GetProperty("presentation").GetString());
    }

    // ---- Range ----

    [Fact]
    public async Task A_range_renders_both_bounds_and_is_always_plural()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var recipeId = await CreateAsync(client, _fixture.WorkspaceA);

        var body = await ReadAsync(await client.PostAsJsonAsync(
            NormalizeDisplayOf(_fixture.WorkspaceA, recipeId),
            new { sourceVersionNumber = 1, value = 1m, upperValue = 1.5m, unitId = _cupUsId, precision = 2, useAbbreviation = false, rounding = "ToEven" },
            TestContext.Current.CancellationToken));

        var result = body.GetProperty("result");
        Assert.Equal("1–1½ cups", result.GetProperty("text").GetString());
        Assert.True(result.TryGetProperty("upperPresentation", out var upperPresentation));
        Assert.Equal("Fraction", upperPresentation.GetString());
    }

    [Fact]
    public async Task A_single_value_carries_no_upper_presentation()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var recipeId = await CreateAsync(client, _fixture.WorkspaceA);

        var body = await ReadAsync(await client.PostAsJsonAsync(
            NormalizeDisplayOf(_fixture.WorkspaceA, recipeId),
            new { sourceVersionNumber = 1, value = 1m, unitId = _cupUsId, precision = 2, useAbbreviation = false, rounding = "ToEven" },
            TestContext.Current.CancellationToken));

        Assert.Equal(JsonValueKind.Null, body.GetProperty("result").GetProperty("upperPresentation").ValueKind);
    }

    // ---- System ----

    [Fact]
    public async Task A_us_customary_unit_renders_its_own_abbreviation()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var recipeId = await CreateAsync(client, _fixture.WorkspaceA);

        var body = await ReadAsync(await client.PostAsJsonAsync(
            NormalizeDisplayOf(_fixture.WorkspaceA, recipeId),
            new { sourceVersionNumber = 1, value = 2m, unitId = _cupUsId, precision = 2, useAbbreviation = true, rounding = "ToEven" },
            TestContext.Current.CancellationToken));

        Assert.Equal("2 c", body.GetProperty("result").GetProperty("text").GetString());
    }

    [Fact]
    public async Task A_metric_unit_renders_its_own_abbreviation()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var recipeId = await CreateAsync(client, _fixture.WorkspaceA);

        var body = await ReadAsync(await client.PostAsJsonAsync(
            NormalizeDisplayOf(_fixture.WorkspaceA, recipeId),
            new { sourceVersionNumber = 1, value = 500m, unitId = _gramId, precision = 0, useAbbreviation = true, rounding = "ToEven" },
            TestContext.Current.CancellationToken));

        Assert.Equal("500 g", body.GetProperty("result").GetProperty("text").GetString());
    }

    // ---- Refusals ----

    [Fact]
    public async Task A_source_version_below_one_is_refused_before_the_recipe_is_read()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);

        var response = await client.PostAsJsonAsync(
            NormalizeDisplayOf(_fixture.WorkspaceA, Guid.NewGuid()),
            new { sourceVersionNumber = 0, value = 1.5m, unitId = _cupUsId, precision = 2, useAbbreviation = false, rounding = "ToEven" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            RecipeErrorCodes.DisplayNormalizationInvalidRequest,
            (await ReadAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task An_upper_value_not_greater_than_the_value_is_refused()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var recipeId = await CreateAsync(client, _fixture.WorkspaceA);

        var response = await client.PostAsJsonAsync(
            NormalizeDisplayOf(_fixture.WorkspaceA, recipeId),
            new { sourceVersionNumber = 1, value = 2m, upperValue = 2m, unitId = _cupUsId, precision = 2, useAbbreviation = false, rounding = "ToEven" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await ReadAsync(response);
        Assert.Equal(RecipeErrorCodes.DisplayNormalizationInvalidRequest, body.GetProperty("code").GetString());
        Assert.True(body.GetProperty("errors").TryGetProperty("upperValue", out _));
    }

    [Fact]
    public async Task An_unknown_unit_id_is_refused_naming_the_field()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var recipeId = await CreateAsync(client, _fixture.WorkspaceA);

        var response = await client.PostAsJsonAsync(
            NormalizeDisplayOf(_fixture.WorkspaceA, recipeId),
            new { sourceVersionNumber = 1, value = 1.5m, unitId = Guid.NewGuid(), precision = 2, useAbbreviation = false, rounding = "ToEven" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await ReadAsync(response);
        Assert.Equal(RecipeErrorCodes.DisplayNormalizationInvalidRequest, body.GetProperty("code").GetString());
        Assert.True(body.GetProperty("errors").TryGetProperty("unitId", out _));
    }

    [Fact]
    public async Task A_version_number_the_recipe_does_not_have_names_the_parameter_at_fault()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var recipeId = await CreateAsync(client, _fixture.WorkspaceA);

        var response = await client.PostAsJsonAsync(
            NormalizeDisplayOf(_fixture.WorkspaceA, recipeId),
            new { sourceVersionNumber = 9, value = 1.5m, unitId = _cupUsId, precision = 2, useAbbreviation = false, rounding = "ToEven" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var body = await ReadAsync(response);
        Assert.Equal(RecipeErrorCodes.VersionNotFound, body.GetProperty("code").GetString());
        Assert.True(body.GetProperty("errors").TryGetProperty("sourceVersionNumber", out _));
    }

    // ---- Authorization ----

    [Fact]
    public async Task Every_member_including_a_viewer_may_normalize_display()
    {
        using var owner = await SignInAsync(_fixture.WorkspaceB);
        var recipeId = await CreateAsync(owner, _fixture.WorkspaceB);

        using var member = await SignInAsync(_fixture.WorkspaceB, asOwner: false);

        var response = await member.PostAsJsonAsync(
            NormalizeDisplayOf(_fixture.WorkspaceB, recipeId),
            new { sourceVersionNumber = 1, value = 1.5m, unitId = _cupUsId, precision = 2, useAbbreviation = false, rounding = "ToEven" },
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
        var request = new { sourceVersionNumber = 1, value = 1.5m, unitId = _cupUsId, precision = 2, useAbbreviation = false, rounding = "ToEven" };

        var unknown = await ownerA.PostAsJsonAsync(NormalizeDisplayOf(_fixture.WorkspaceA, Guid.NewGuid()), request, token);
        var othersRecipe = await ownerA.PostAsJsonAsync(NormalizeDisplayOf(_fixture.WorkspaceA, recipeInB), request, token);

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, othersRecipe.StatusCode);

        var unknownBody = await ReadAsync(unknown);
        var othersBody = await ReadAsync(othersRecipe);

        Assert.Equal(RecipeErrorCodes.RecipeNotFound, unknownBody.GetProperty("code").GetString());
        Assert.Equal(WithoutTrace(unknownBody), WithoutTrace(othersBody));
    }

    [Fact]
    public async Task A_member_of_one_workspace_cannot_normalize_display_through_the_other_slug()
    {
        using var ownerB = await SignInAsync(_fixture.WorkspaceB);
        var recipeInB = await CreateAsync(ownerB, _fixture.WorkspaceB);

        using var ownerA = await SignInAsync(_fixture.WorkspaceA);

        var response = await ownerA.PostAsJsonAsync(
            NormalizeDisplayOf(_fixture.WorkspaceB, recipeInB),
            new { sourceVersionNumber = 1, value = 1.5m, unitId = _cupUsId, precision = 2, useAbbreviation = false, rounding = "ToEven" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- Helpers ----

    private async Task<Guid> SeedUnitAsync(
        string code,
        string displayName,
        string pluralName,
        string abbreviation,
        MeasurementDimension dimension,
        MeasurementSystem system,
        decimal? baseUnitFactor,
        int displayPrecision)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        var unit = new MeasurementUnit
        {
            Id = Guid.NewGuid(),
            Code = code,
            DisplayName = displayName,
            PluralName = pluralName,
            Abbreviation = abbreviation,
            Dimension = dimension,
            System = system,
            BaseUnitFactor = baseUnitFactor,
            DisplayPrecision = displayPrecision,
            IsActive = true,
        };
        db.MeasurementUnits.Add(unit);
        await db.SaveChangesAsync();

        return unit.Id;
    }

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

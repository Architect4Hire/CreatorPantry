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
/// <c>POST /api/v1/workspaces/{workspaceSlug}/recipes/{recipeId}/calculations/convert-units</c> through the
/// real Gateway — real cookie session, real gateway-signed internal token, real API — for source/provenance,
/// the requests it refuses, and what one workspace can learn about the other's recipes (ING-004, 7.11a).
/// </summary>
public sealed class RecipeUnitConversionEndpointTests : IAsyncLifetime
{
    private TwoWorkspaceGatewayFixture _fixture = null!;
    private Guid _gramId;
    private Guid _kilogramId;
    private Guid _cupUsId;

    public async ValueTask InitializeAsync()
    {
        _fixture = await TwoWorkspaceGatewayFixture.CreateAsync();

        _gramId = await SeedUnitAsync("g", "gram", "grams", "g", MeasurementDimension.Mass, MeasurementSystem.Metric, 1m, 0);
        _kilogramId = await SeedUnitAsync("kg", "kilogram", "kilograms", "kg", MeasurementDimension.Mass, MeasurementSystem.Metric, 1000m, 3);
        _cupUsId = await SeedUnitAsync(
            "cup-us", "cup", "cups", "c", MeasurementDimension.Volume, MeasurementSystem.UsCustomary, 236.5882365m, 2);
    }

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static string ConvertUnitsOf(SeededWorkspace workspace, Guid recipeId) =>
        $"/api/v1/workspaces/{workspace.Slug}/recipes/{recipeId}/calculations/convert-units";

    // ---- Source and provenance ----

    [Fact]
    public async Task A_same_dimension_conversion_reports_the_source_version_formula_and_converted_quantity()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var recipeId = await CreateAsync(client, _fixture.WorkspaceA);

        var body = await ReadAsync(await client.PostAsJsonAsync(
            ConvertUnitsOf(_fixture.WorkspaceA, recipeId),
            new { sourceVersionNumber = 1, quantity = 1000m, fromUnitId = _gramId, toUnitId = _kilogramId },
            TestContext.Current.CancellationToken));

        Assert.Equal(1, body.GetProperty("sourceVersionNumber").GetInt32());

        var result = body.GetProperty("result");
        Assert.Equal("SameDimensionFactor", result.GetProperty("method").GetString());
        Assert.Equal(1m, result.GetProperty("convertedDisplayQuantity").GetDecimal());
        Assert.False(string.IsNullOrWhiteSpace(result.GetProperty("formula").GetString()));
        Assert.Equal(JsonValueKind.Null, result.GetProperty("source").ValueKind);
    }

    [Fact]
    public async Task A_second_version_is_named_as_the_source_when_asked_for()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var recipeId = await CreateAsync(client, _fixture.WorkspaceA);
        await EditAsync(client, _fixture.WorkspaceA, recipeId, "Olive oil and rosemary cake");

        var body = await ReadAsync(await client.PostAsJsonAsync(
            ConvertUnitsOf(_fixture.WorkspaceA, recipeId),
            new { sourceVersionNumber = 2, quantity = 1000m, fromUnitId = _gramId, toUnitId = _kilogramId },
            TestContext.Current.CancellationToken));

        Assert.Equal(2, body.GetProperty("sourceVersionNumber").GetInt32());
    }

    // ---- Refusals ----

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    public async Task A_non_positive_quantity_is_refused(decimal quantity)
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var recipeId = await CreateAsync(client, _fixture.WorkspaceA);

        var response = await client.PostAsJsonAsync(
            ConvertUnitsOf(_fixture.WorkspaceA, recipeId),
            new { sourceVersionNumber = 1, quantity, fromUnitId = _gramId, toUnitId = _kilogramId },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            RecipeErrorCodes.UnitConversionInvalidRequest,
            (await ReadAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_source_version_below_one_is_refused_before_the_recipe_is_read()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);

        var response = await client.PostAsJsonAsync(
            ConvertUnitsOf(_fixture.WorkspaceA, Guid.NewGuid()),
            new { sourceVersionNumber = 0, quantity = 1000m, fromUnitId = _gramId, toUnitId = _kilogramId },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            RecipeErrorCodes.UnitConversionInvalidRequest,
            (await ReadAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task An_unknown_unit_id_is_refused_naming_the_field()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var recipeId = await CreateAsync(client, _fixture.WorkspaceA);

        var response = await client.PostAsJsonAsync(
            ConvertUnitsOf(_fixture.WorkspaceA, recipeId),
            new { sourceVersionNumber = 1, quantity = 1000m, fromUnitId = _gramId, toUnitId = Guid.NewGuid() },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await ReadAsync(response);
        Assert.Equal(RecipeErrorCodes.UnitConversionInvalidRequest, body.GetProperty("code").GetString());
        Assert.True(body.GetProperty("errors").TryGetProperty("toUnitId", out _));
    }

    /// <summary>ING-004's own restriction: cross-dimension conversion without an approved density is refused, not invented.</summary>
    [Fact]
    public async Task A_cross_dimension_conversion_without_density_is_refused()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var recipeId = await CreateAsync(client, _fixture.WorkspaceA);

        var response = await client.PostAsJsonAsync(
            ConvertUnitsOf(_fixture.WorkspaceA, recipeId),
            new { sourceVersionNumber = 1, quantity = 2m, fromUnitId = _cupUsId, toUnitId = _gramId },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await ReadAsync(response);
        Assert.Equal(RecipeErrorCodes.UnitConversionInvalidRequest, body.GetProperty("code").GetString());
        Assert.True(body.GetProperty("errors").TryGetProperty("toUnitId", out _));
    }

    [Fact]
    public async Task A_version_number_the_recipe_does_not_have_names_the_parameter_at_fault()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);
        var recipeId = await CreateAsync(client, _fixture.WorkspaceA);

        var response = await client.PostAsJsonAsync(
            ConvertUnitsOf(_fixture.WorkspaceA, recipeId),
            new { sourceVersionNumber = 9, quantity = 1000m, fromUnitId = _gramId, toUnitId = _kilogramId },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var body = await ReadAsync(response);
        Assert.Equal(RecipeErrorCodes.VersionNotFound, body.GetProperty("code").GetString());
        Assert.True(body.GetProperty("errors").TryGetProperty("sourceVersionNumber", out _));
    }

    // ---- Authorization ----

    [Fact]
    public async Task Every_member_including_a_viewer_may_convert_units()
    {
        using var owner = await SignInAsync(_fixture.WorkspaceB);
        var recipeId = await CreateAsync(owner, _fixture.WorkspaceB);

        using var member = await SignInAsync(_fixture.WorkspaceB, asOwner: false);

        var response = await member.PostAsJsonAsync(
            ConvertUnitsOf(_fixture.WorkspaceB, recipeId),
            new { sourceVersionNumber = 1, quantity = 1000m, fromUnitId = _gramId, toUnitId = _kilogramId },
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
        var request = new { sourceVersionNumber = 1, quantity = 1000m, fromUnitId = _gramId, toUnitId = _kilogramId };

        var unknown = await ownerA.PostAsJsonAsync(ConvertUnitsOf(_fixture.WorkspaceA, Guid.NewGuid()), request, token);
        var othersRecipe = await ownerA.PostAsJsonAsync(ConvertUnitsOf(_fixture.WorkspaceA, recipeInB), request, token);

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, othersRecipe.StatusCode);

        var unknownBody = await ReadAsync(unknown);
        var othersBody = await ReadAsync(othersRecipe);

        Assert.Equal(RecipeErrorCodes.RecipeNotFound, unknownBody.GetProperty("code").GetString());
        Assert.Equal(WithoutTrace(unknownBody), WithoutTrace(othersBody));
    }

    [Fact]
    public async Task A_member_of_one_workspace_cannot_convert_units_through_the_other_slug()
    {
        using var ownerB = await SignInAsync(_fixture.WorkspaceB);
        var recipeInB = await CreateAsync(ownerB, _fixture.WorkspaceB);

        using var ownerA = await SignInAsync(_fixture.WorkspaceA);

        var response = await ownerA.PostAsJsonAsync(
            ConvertUnitsOf(_fixture.WorkspaceB, recipeInB),
            new { sourceVersionNumber = 1, quantity = 1000m, fromUnitId = _gramId, toUnitId = _kilogramId },
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

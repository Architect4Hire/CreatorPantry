using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Modules.Ingredients.Data.Entities;
using CreatorPantry.Domain.Modules.Ingredients.Gateways;
using CreatorPantry.Domain.Modules.Ingredients.Managers;
using CreatorPantry.Domain.Modules.Measurement.Data.Entities;
using CreatorPantry.Domain.Modules.Measurement.Managers;
using CreatorPantry.Domain.Modules.Nutrition.Data.Entities;
using CreatorPantry.Domain.Modules.Nutrition.Managers;
using CreatorPantry.Tests.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Nutrition;

/// <summary>
/// <c>POST .../recipes/{recipeId}/versions/{versionNumber}/analysis/nutrition</c> through the real Gateway (14.6a):
/// which mappings count, how complete the estimate is and whether it may be published, the yield basis, every
/// refusal, and what another workspace can see. The nutrition source is scripted; nothing leaves the process.
/// </summary>
public sealed class NutritionEstimateEndpointTests : IAsyncLifetime
{
    private const string FlourRecord = "169761";
    private const string SugarRecord = "169655";
    private const string FdcVersion = "sr-legacy/2019-04-01";

    private readonly ScriptedEvidence _evidence = new();
    private TwoWorkspaceGatewayFixture _fixture = null!;
    private Reference _reference = null!;

    public async ValueTask InitializeAsync()
    {
        _fixture = await TwoWorkspaceGatewayFixture.CreateAsync(services => services.AddSingleton<IReferenceEvidenceGateway>(_evidence));
        _reference = await SeedReferenceAsync();

        _evidence.Has(FlourRecord, "Wheat flour, white, all-purpose", FdcVersion, [], [Nutrient(NutrientCode.Energy, 364m, NutrientUnit.Kilocalorie), Nutrient(NutrientCode.Protein, 10.3m, NutrientUnit.Gram)]);
        _evidence.Has(SugarRecord, "Sugars, granulated", FdcVersion, [], [Nutrient(NutrientCode.Energy, 387m, NutrientUnit.Kilocalorie)]);
    }

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- Mapping and coverage ----

    [Fact]
    public async Task A_fully_mapped_version_is_a_complete_publishable_estimate_labelled_as_one()
    {
        await MapAsync(_fixture.WorkspaceA.Id, _reference.Flour, FlourRecord);
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);
        var recipeId = await CreateAsync(client, _fixture.WorkspaceA, new { servingCount = 4 }, ("200 g flour", 200m, _reference.Flour));

        var response = await EstimateAsync(client, _fixture.WorkspaceA, recipeId, 1, new { });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore, "an estimate describes the mappings and records as they stood");

        var body = await BodyOf(response);
        Assert.Equal(
            ("Estimate", NutritionEstimatePolicy.MethodVersion, NutritionEstimatePolicy.Method),
            (body.GetProperty("label").GetString(), body.GetProperty("methodVersion").GetString(), body.GetProperty("method").GetString()));
        Assert.Equal(("Complete", true, false), (body.GetProperty("status").GetString(), body.GetProperty("publishable").GetBoolean(), body.GetProperty("partial").GetBoolean()));
        Assert.Equal("Mass", body.GetProperty("confidence").GetString());
        Assert.Equal(await VersionIdAsync(client, _fixture.WorkspaceA, recipeId, 1), body.GetProperty("recipeVersionId").GetGuid());

        var energy = Nutrient(body, "Energy");
        Assert.Equal(728m, energy.GetProperty("wholeRecipe").GetProperty("low").GetDecimal());
        Assert.Equal(182m, energy.GetProperty("perServing").GetProperty("low").GetDecimal());

        var source = Assert.Single(body.GetProperty("sources").EnumerateArray());
        Assert.Equal(("usda-fdc", "CC0-1.0"), (source.GetProperty("sourceCode").GetString(), source.GetProperty("licenseCode").GetString()));

        var unresolved = body.GetProperty("unresolved");
        Assert.All(["unmapped", "needsReview", "unconvertible"], group => Assert.Equal(0, unresolved.GetProperty(group).GetArrayLength()));
    }

    [Fact]
    public async Task Unresolved_mappings_stay_explicit_and_make_the_estimate_unpublishable()
    {
        await MapAsync(_fixture.WorkspaceA.Id, _reference.Flour, FlourRecord);
        await MapAsync(_fixture.WorkspaceA.Id, _reference.Sugar, SugarRecord, NutritionMappingStatus.Candidate);

        // Workspace B has confirmed butter; that is B's decision, and never counts in A.
        await MapAsync(_fixture.WorkspaceB.Id, _reference.Butter, SugarRecord);

        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);
        var recipeId = await CreateAsync(client, _fixture.WorkspaceA, new { servingCount = 4 },
            ("200 g flour", 200m, _reference.Flour),
            ("50 g sugar", 50m, _reference.Sugar),
            ("30 g butter", 30m, _reference.Butter),
            ("10 g house spice", 10m, null));

        var body = await BodyOf(await EstimateAsync(client, _fixture.WorkspaceA, recipeId, 1, new { }));

        Assert.Equal(("Incomplete", false, true), (body.GetProperty("status").GetString(), body.GetProperty("publishable").GetBoolean(), body.GetProperty("partial").GetBoolean()));
        Assert.Equal(
            [("Included", "None"), ("Unmapped", "MappingNotConfirmed"), ("Unmapped", "NoMapping"), ("Unmapped", "NoIngredient")],
            body.GetProperty("lines").EnumerateArray().Select(line => (line.GetProperty("outcome").GetString(), line.GetProperty("reason").GetString())));

        var lineIds = body.GetProperty("lines").EnumerateArray().Select(line => line.GetProperty("lineId").GetGuid()).ToList();
        Assert.Equal(lineIds[1..], body.GetProperty("unresolved").GetProperty("unmapped").EnumerateArray().Select(id => id.GetGuid()));

        // A lower bound: only the flour is counted.
        Assert.Equal(728m, Nutrient(body, "Energy").GetProperty("wholeRecipe").GetProperty("low").GetDecimal());
    }

    [Fact]
    public async Task A_changed_or_withdrawn_reference_needs_review_and_is_not_counted()
    {
        const string Revised = "170000";
        const string Withdrawn = "170001";

        // The source now publishes the confirmed record under a newer version; another it no longer has at all.
        _evidence.Has(Revised, "Butter, salted", "sr-legacy/2024-04-01", [], [Nutrient(NutrientCode.Energy, 717m, NutrientUnit.Kilocalorie)]);
        _evidence.Answers(Withdrawn, ReferenceEvidenceOutcome.NotFound);

        await MapAsync(_fixture.WorkspaceA.Id, _reference.Flour, FlourRecord);
        await MapAsync(_fixture.WorkspaceA.Id, _reference.Butter, Revised);
        await MapAsync(_fixture.WorkspaceA.Id, _reference.Sugar, Withdrawn);

        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);
        var recipeId = await CreateAsync(client, _fixture.WorkspaceA, new { },
            ("200 g flour", 200m, _reference.Flour), ("30 g butter", 30m, _reference.Butter), ("50 g sugar", 50m, _reference.Sugar));

        var body = await BodyOf(await EstimateAsync(client, _fixture.WorkspaceA, recipeId, 1, new { }));

        Assert.Equal(
            [("Included", "None"), ("NeedsReview", "SourceVersionChanged"), ("NeedsReview", "ReferenceWithdrawn")],
            body.GetProperty("lines").EnumerateArray().Select(line => (line.GetProperty("outcome").GetString(), line.GetProperty("reason").GetString())));
        Assert.Equal(2, body.GetProperty("unresolved").GetProperty("needsReview").GetArrayLength());
        Assert.False(body.GetProperty("publishable").GetBoolean());
    }

    // ---- The yield basis ----

    [Fact]
    public async Task A_requested_serving_count_overrides_the_versions_own()
    {
        await MapAsync(_fixture.WorkspaceA.Id, _reference.Flour, FlourRecord);
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);
        var recipeId = await CreateAsync(client, _fixture.WorkspaceA, new { servingCount = 4 }, ("200 g flour", 200m, _reference.Flour));

        var body = await BodyOf(await EstimateAsync(client, _fixture.WorkspaceA, recipeId, 1, new { servings = 8 }));

        Assert.Equal(("Requested", 8m), (body.GetProperty("servingsSource").GetString(), body.GetProperty("servings").GetDecimal()));
        Assert.Equal(91m, Nutrient(body, "Energy").GetProperty("perServing").GetProperty("low").GetDecimal());
    }

    [Fact]
    public async Task A_version_with_a_batch_and_a_serving_size_divides_one_by_the_other()
    {
        await MapAsync(_fixture.WorkspaceA.Id, _reference.Flour, FlourRecord);
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);
        var recipeId = await CreateAsync(client, _fixture.WorkspaceA,
            new { yieldText = "Makes 1 kg", yieldQuantity = 1000, yieldUnitId = _reference.Gram, servingSize = 250 },
            ("200 g flour", 200m, _reference.Flour));

        var body = await BodyOf(await EstimateAsync(client, _fixture.WorkspaceA, recipeId, 1, new { servings = (decimal?)null }));

        Assert.Equal(("YieldOverServingSize", 4m), (body.GetProperty("servingsSource").GetString(), body.GetProperty("servings").GetDecimal()));
    }

    [Fact]
    public async Task A_version_with_no_serving_basis_gives_whole_recipe_figures_only()
    {
        await MapAsync(_fixture.WorkspaceA.Id, _reference.Flour, FlourRecord);
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);
        var recipeId = await CreateAsync(client, _fixture.WorkspaceA, new { }, ("200 g flour", 200m, _reference.Flour));

        var body = await BodyOf(await EstimateAsync(client, _fixture.WorkspaceA, recipeId, 1, new { }));

        Assert.Equal("None", body.GetProperty("servingsSource").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("servings").ValueKind);
        Assert.Equal(JsonValueKind.Null, Nutrient(body, "Energy").GetProperty("perServing").ValueKind);
    }

    // ---- Who may read, and what another workspace sees ----

    [Fact]
    public async Task A_viewer_may_read_an_estimate()
    {
        using var owner = await _fixture.SignInAsync(_fixture.WorkspaceB.OwnerEmail, cancellationToken: Ct);
        var recipeId = await CreateAsync(owner, _fixture.WorkspaceB, new { }, ("200 g flour", 200m, _reference.Flour));

        using var viewer = await _fixture.SignInAsync(_fixture.WorkspaceB.MemberEmail, cancellationToken: Ct);

        Assert.Equal(HttpStatusCode.OK, (await EstimateAsync(viewer, _fixture.WorkspaceB, recipeId, 1, new { })).StatusCode);
    }

    [Fact]
    public async Task Another_workspaces_recipe_is_not_found_and_a_non_member_cannot_reach_the_workspace()
    {
        using var ownerA = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);
        var recipeA = await CreateAsync(ownerA, _fixture.WorkspaceA, new { }, ("200 g flour", 200m, _reference.Flour));

        using var ownerB = await _fixture.SignInAsync(_fixture.WorkspaceB.OwnerEmail, cancellationToken: Ct);

        // A workspace id smuggled into the body is not read: the workspace is the route's and the caller's membership.
        var underB = await EstimateAsync(ownerB, _fixture.WorkspaceB, recipeA, 1, new { workspaceId = _fixture.WorkspaceA.Id });
        Assert.Equal(HttpStatusCode.NotFound, underB.StatusCode);
        Assert.Equal("recipes.recipe.not_found", (await BodyOf(underB)).GetProperty("code").GetString());

        Assert.Equal(HttpStatusCode.NotFound, (await EstimateAsync(ownerB, _fixture.WorkspaceA, recipeA, 1, new { })).StatusCode);
    }

    // ---- Refusals ----

    [Theory]
    [InlineData("0")]
    [InlineData("-2")]
    [InlineData("10001")]
    [InlineData("1.23456")]
    public async Task A_serving_count_that_cannot_be_used_is_a_bad_request_naming_it(string servings)
    {
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);
        var recipeId = await CreateAsync(client, _fixture.WorkspaceA, new { }, ("200 g flour", 200m, _reference.Flour));

        var response = await client.PostAsync(
            EstimateRoute(_fixture.WorkspaceA, recipeId, 1),
            new StringContent($$"""{ "servings": {{servings}} }""", System.Text.Encoding.UTF8, "application/json"),
            cancellationToken: Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await BodyOf(response);
        Assert.Equal("nutrition.estimate.invalid", problem.GetProperty("code").GetString());
        Assert.True(problem.GetProperty("errors").EnumerateObject().Any(error => error.Name.Equals("servings", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task Trailing_zeros_do_not_count_as_decimal_places()
    {
        await MapAsync(_fixture.WorkspaceA.Id, _reference.Flour, FlourRecord);
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);
        var recipeId = await CreateAsync(client, _fixture.WorkspaceA, new { }, ("200 g flour", 200m, _reference.Flour));

        var response = await client.PostAsync(
            EstimateRoute(_fixture.WorkspaceA, recipeId, 1),
            new StringContent("""{ "servings": 8.00000 }""", System.Text.Encoding.UTF8, "application/json"),
            cancellationToken: Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(8m, (await BodyOf(response)).GetProperty("servings").GetDecimal());
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    // "8" as a string is read as 8: the API reads JSON with the web defaults, which accept numbers written as
    // strings, and the published schema says so. A string that is not a number is malformed.
    [InlineData("""{ "servings": "eight" }""")]
    public async Task A_missing_null_or_mistyped_body_is_malformed_as_on_every_route(string json)
    {
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);
        var recipeId = await CreateAsync(client, _fixture.WorkspaceA, new { }, ("200 g flour", 200m, _reference.Flour));

        var response = await client.PostAsync(
            EstimateRoute(_fixture.WorkspaceA, recipeId, 1), new StringContent(json, System.Text.Encoding.UTF8, "application/json"), cancellationToken: Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("request.malformed", (await BodyOf(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_version_number_below_one_matches_no_route()
    {
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);
        var recipeId = await CreateAsync(client, _fixture.WorkspaceA, new { }, ("200 g flour", 200m, _reference.Flour));

        Assert.Equal(HttpStatusCode.NotFound, (await EstimateAsync(client, _fixture.WorkspaceA, recipeId, 0, new { })).StatusCode);
    }

    [Fact]
    public async Task A_source_not_configured_here_is_unavailable_and_says_it_will_not_clear_by_waiting()
    {
        await MapAsync(_fixture.WorkspaceA.Id, _reference.Flour, FlourRecord);
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);
        var recipeId = await CreateAsync(client, _fixture.WorkspaceA, new { }, ("200 g flour", 200m, _reference.Flour));

        _evidence.Answers(FlourRecord, ReferenceEvidenceOutcome.NotConfigured);
        var response = await EstimateAsync(client, _fixture.WorkspaceA, recipeId, 1, new { });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var problem = await BodyOf(response);
        Assert.Equal("nutrition.estimate.source.unavailable", problem.GetProperty("code").GetString());
        Assert.Contains("not available on this installation", problem.GetProperty("title").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_version_number_is_not_found_and_names_it()
    {
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);
        var recipeId = await CreateAsync(client, _fixture.WorkspaceA, new { }, ("200 g flour", 200m, _reference.Flour));

        var response = await EstimateAsync(client, _fixture.WorkspaceA, recipeId, 9, new { });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var problem = await BodyOf(response);
        Assert.Equal("recipes.version.not_found", problem.GetProperty("code").GetString());
        Assert.True(problem.GetProperty("errors").TryGetProperty("versionNumber", out _));
    }

    [Fact]
    public async Task A_source_that_cannot_be_asked_is_unavailable_and_no_smaller_estimate_is_given()
    {
        await MapAsync(_fixture.WorkspaceA.Id, _reference.Flour, FlourRecord);
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);
        var recipeId = await CreateAsync(client, _fixture.WorkspaceA, new { }, ("200 g flour", 200m, _reference.Flour));

        _evidence.Down = true;
        var response = await EstimateAsync(client, _fixture.WorkspaceA, recipeId, 1, new { });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("nutrition.estimate.source.unavailable", (await BodyOf(response)).GetProperty("code").GetString());
    }

    // ---- Helpers ----

    private sealed record Reference(Guid Gram, Guid Flour, Guid Sugar, Guid Butter, Guid FdcSource);

    private static NutrientEvidence Nutrient(NutrientCode code, decimal per100, NutrientUnit unit) => new(code, per100, unit, code.ToString(), null);

    private static JsonElement Nutrient(JsonElement body, string code) =>
        body.GetProperty("nutrients").EnumerateArray().Single(nutrient => nutrient.GetProperty("code").GetString() == code);

    private static string EstimateRoute(SeededWorkspace workspace, Guid recipeId, int versionNumber) =>
        $"/api/v1/workspaces/{workspace.Slug}/recipes/{recipeId}/versions/{versionNumber}/analysis/nutrition";

    private static Task<HttpResponseMessage> EstimateAsync(GatewayClient client, SeededWorkspace workspace, Guid recipeId, int versionNumber, object body) =>
        client.PostAsJsonAsync(EstimateRoute(workspace, recipeId, versionNumber), body, Ct);

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    private async Task<Guid> CreateAsync(
        GatewayClient client, SeededWorkspace workspace, object yield, params (string Text, decimal Grams, Guid? IngredientId)[] lines)
    {
        // The yield fields travel beside the title and lines; merged as JSON so each test names only what it sets.
        var body = JsonSerializer.SerializeToNode(yield, JsonSerializerOptions.Web)!.AsObject();
        body["title"] = "Shortbread";
        body["ingredientGroups"] = JsonSerializer.SerializeToNode(new[]
        {
            new
            {
                ingredients = lines.Select(line => new
                {
                    displayText = line.Text,
                    quantity = line.Grams,
                    measurementUnitId = _reference.Gram,
                    ingredientId = line.IngredientId,
                }).ToArray(),
            },
        }, JsonSerializerOptions.Web);

        var created = await client.PostAsJsonAsync($"/api/v1/workspaces/{workspace.Slug}/recipes", body, Ct);
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));

        return (await BodyOf(await client.GetAsync(created.Headers.Location!.ToString(), Ct))).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> VersionIdAsync(GatewayClient client, SeededWorkspace workspace, Guid recipeId, int versionNumber)
    {
        var history = await BodyOf(await client.GetAsync($"/api/v1/workspaces/{workspace.Slug}/recipes/{recipeId}/versions", Ct));

        return history.GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("versionNumber").GetInt32() == versionNumber)
            .GetProperty("id").GetGuid();
    }

    /// <summary>A workspace's mapping of one ingredient to an FDC record, written as the confirm command would leave it.</summary>
    private async Task MapAsync(Guid workspaceId, Guid ingredientId, string recordId, NutritionMappingStatus status = NutritionMappingStatus.Confirmed)
    {
        var confirmed = status is NutritionMappingStatus.Confirmed;
        var at = DateTimeOffset.UtcNow;

        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        db.NutritionMappings.Add(new NutritionMapping
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            IngredientId = ingredientId,
            Status = status,
            MatchMethod = confirmed ? NutritionMatchMethod.CreatorSelected : NutritionMatchMethod.AutomaticSearch,
            Confidence = confirmed ? null : NutritionMatchConfidence.High,
            ReferenceSourceId = _reference.FdcSource,
            SourceCode = EvidenceSourceCatalog.UsdaFoodDataCentral.Code,
            ExternalRecordId = recordId,
            ReferenceDescription = "A recorded FDC food",
            SourceVersion = FdcVersion,
            ConfirmedAt = confirmed ? at : null,
            ConfirmedByMembershipId = confirmed ? Guid.NewGuid() : null,
            CreatedAt = at,
            CreatedByMembershipId = Guid.NewGuid(),
            UpdatedAt = at,
            UpdatedByMembershipId = Guid.NewGuid(),
        });

        await db.SaveChangesAsync(Ct);
    }

    /// <summary>Platform reference rows no other test uses, and the FDC source row a mapping names.</summary>
    private async Task<Reference> SeedReferenceAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];

        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        var fdc = EvidenceSourceCatalog.UsdaFoodDataCentral;
        var fdcSource = await db.ReferenceSources.Where(source => source.Code == fdc.Code).Select(source => (Guid?)source.Id).SingleOrDefaultAsync(Ct);
        if (fdcSource is null)
        {
            fdcSource = Guid.NewGuid();
            db.ReferenceSources.Add(new ReferenceSource
            {
                Id = fdcSource.Value, Code = fdc.Code, Name = fdc.Name, Kind = fdc.Kind, Url = fdc.Url, Citation = fdc.Citation,
                LicenseCode = fdc.LicenseCode, LicenseUrl = fdc.LicenseUrl,
            });
        }

        var reference = new Reference(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), fdcSource.Value);

        db.MeasurementUnits.Add(new MeasurementUnit
        {
            Id = reference.Gram,
            Code = $"g-{suffix}",
            DisplayName = "gram",
            PluralName = "grams",
            Abbreviation = "g",
            Dimension = MeasurementDimension.Mass,
            System = MeasurementSystem.Metric,
            BaseUnitFactor = 1m,
            DisplayPrecision = 0,
        });

        foreach (var (id, name) in new[] { (reference.Flour, "flour"), (reference.Sugar, "sugar"), (reference.Butter, "butter") })
        {
            db.Ingredients.Add(new Ingredient { Id = id, CanonicalName = $"{name} {suffix}", NormalizedName = $"{name} {suffix}", SearchText = $"{name} {suffix}" });
        }

        await db.SaveChangesAsync(Ct);

        return reference;
    }
}

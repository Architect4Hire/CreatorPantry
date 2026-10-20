using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Ingredients.Data.Entities;
using CreatorPantry.Domain.Modules.Ingredients.Managers;
using CreatorPantry.Domain.Modules.Nutrition.Managers;
using CreatorPantry.Domain.Modules.Vocabulary.Data.Entities;
using CreatorPantry.Tests.Tenancy;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Nutrition;

/// <summary>
/// <c>POST .../recipes/{recipeId}/versions/{versionNumber}/analysis/dietary-allergen</c> through the real Gateway
/// (14.6): findings for profiles and allergens, the evidence and sources behind them, what is unknown, how complete
/// the records are, every refusal, and what another workspace can see.
/// </summary>
public sealed class DietaryAllergenAnalysisEndpointTests : IAsyncLifetime
{
    private TwoWorkspaceGatewayFixture _fixture = null!;
    private Reference _reference = null!;

    public async ValueTask InitializeAsync()
    {
        _fixture = await TwoWorkspaceGatewayFixture.CreateAsync();
        _reference = await SeedReferenceAsync();
    }

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- Profiles, allergens, evidence and sources ----

    [Fact]
    public async Task A_member_reads_findings_with_their_evidence_sources_coverage_and_disclaimer()
    {
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);
        var recipeId = await CreateAsync(client, _fixture.WorkspaceA,
            ("200 g flour", _reference.Flour), ("50 g sugar", _reference.Sugar), ("a splash of house sauce", null));

        var response = await AnalyzeAsync(client, _fixture.WorkspaceA, recipeId, 1, new { allergenIds = new[] { _reference.Wheat }, dietaryProfileIds = new[] { _reference.Vegan } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore, "an analysis describes the records as they stood when read");

        var body = await BodyOf(response);
        Assert.Equal(recipeId, body.GetProperty("recipeId").GetGuid());
        Assert.Equal(1, body.GetProperty("versionNumber").GetInt32());
        Assert.NotEqual(Guid.Empty, body.GetProperty("recipeVersionId").GetGuid());
        Assert.Equal(DietaryAllergenAnalysisPolicy.MethodVersion, body.GetProperty("methodVersion").GetString());
        Assert.Equal(DietaryAllergenAnalysisPolicy.CrossContactDisclaimer, body.GetProperty("crossContactDisclaimer").GetString());
        // As of today (UTC); a day's slack for a run that crosses midnight.
        var evaluatedOn = DateOnly.Parse(body.GetProperty("evaluatedOn").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(evaluatedOn, DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1), DateOnly.FromDateTime(DateTime.UtcNow));

        // Wheat: the flour line's record says present; the other two lines have none. The finding stands, on
        // partial records, and the unknowns say why.
        var wheat = Assert.Single(body.GetProperty("allergens").EnumerateArray());
        Assert.Equal("Contains", wheat.GetProperty("finding").GetString());
        Assert.Equal("Partial", wheat.GetProperty("confidence").GetString());
        Assert.Equal((3, 1), (wheat.GetProperty("coverage").GetProperty("linesRead").GetInt32(), wheat.GetProperty("coverage").GetProperty("linesWithCountedRecords").GetInt32()));
        Assert.Equal(2, wheat.GetProperty("unknownLines").GetArrayLength());
        Assert.Equal(
            ["None", "NoRecord", "NotARecognisedIngredient"],
            wheat.GetProperty("lines").EnumerateArray().Select(line => line.GetProperty("reason").GetString()));

        var evidence = Assert.Single(wheat.GetProperty("lines")[0].GetProperty("evidence").EnumerateArray());
        Assert.Equal("Present", evidence.GetProperty("presence").GetString());
        Assert.Equal(_reference.SourceCode, evidence.GetProperty("sourceCode").GetString());
        Assert.Equal("A test official list, 2037.", evidence.GetProperty("citation").GetString());
        Assert.True(evidence.GetProperty("counted").GetBoolean());

        // Vegan: the sugar line depends on the product, the others are unknown — and the source's note says why.
        var vegan = Assert.Single(body.GetProperty("dietary").EnumerateArray());
        Assert.Equal("DependsOnProduct", vegan.GetProperty("finding").GetString());
        Assert.Equal("Partial", vegan.GetProperty("confidence").GetString());
        Assert.Equal([recipeLine(body, 1)], vegan.GetProperty("dependsOnProductLines").EnumerateArray().Select(id => id.GetGuid()));
        Assert.Equal("Some cane sugar is filtered through bone char.", vegan.GetProperty("lines")[1].GetProperty("evidence")[0].GetProperty("evidenceNote").GetString());

        // One source, cited once, with its licence and the latest of its records the analysis read.
        var source = Assert.Single(body.GetProperty("sources").EnumerateArray());
        Assert.Equal(_reference.SourceCode, source.GetProperty("code").GetString());
        Assert.Equal("CC0-1.0", source.GetProperty("licenseCode").GetString());
        Assert.Equal("2026-02-01", source.GetProperty("latestEffectiveFrom").GetString());

        static Guid recipeLine(JsonElement body, int index) =>
            body.GetProperty("allergens")[0].GetProperty("lines")[index].GetProperty("lineId").GetGuid();
    }

    [Fact]
    public async Task The_version_number_selects_that_version_and_not_the_recipe_as_it_now_stands()
    {
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);
        var recipeId = await CreateAsync(client, _fixture.WorkspaceA, ("200 g flour", _reference.Flour));
        await ReplaceLinesAsync(client, _fixture.WorkspaceA, recipeId, ("200 g flour", _reference.Flour), ("50 g sugar", _reference.Sugar));

        var selection = new { dietaryProfileIds = new[] { _reference.Vegan } };
        var first = await BodyOf(await AnalyzeAsync(client, _fixture.WorkspaceA, recipeId, 1, selection));
        var second = await BodyOf(await AnalyzeAsync(client, _fixture.WorkspaceA, recipeId, 2, selection));

        Assert.Equal(1, first.GetProperty("dietary")[0].GetProperty("lines").GetArrayLength());
        Assert.Equal("Unknown", first.GetProperty("dietary")[0].GetProperty("finding").GetString());
        Assert.Equal(2, second.GetProperty("dietary")[0].GetProperty("lines").GetArrayLength());
        Assert.Equal("DependsOnProduct", second.GetProperty("dietary")[0].GetProperty("finding").GetString());

        // Each answer names the exact version its number named, as the history lists them.
        var history = await BodyOf(await client.GetAsync($"/api/v1/workspaces/{_fixture.WorkspaceA.Slug}/recipes/{recipeId}/versions", Ct));
        var ids = history.GetProperty("items").EnumerateArray().ToDictionary(item => item.GetProperty("versionNumber").GetInt32(), item => item.GetProperty("id").GetGuid());
        Assert.Equal(ids[1], first.GetProperty("recipeVersionId").GetGuid());
        Assert.Equal(ids[2], second.GetProperty("recipeVersionId").GetGuid());
    }

    [Fact]
    public async Task A_recipe_with_no_records_is_unknown_with_no_confidence_and_no_sources()
    {
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);
        var recipeId = await CreateAsync(client, _fixture.WorkspaceA, ("a splash of house sauce", null));

        var body = await BodyOf(await AnalyzeAsync(client, _fixture.WorkspaceA, recipeId, 1, new { allergenIds = new[] { _reference.Wheat } }));

        var wheat = body.GetProperty("allergens")[0];
        Assert.Equal(("Unknown", "None"), (wheat.GetProperty("finding").GetString(), wheat.GetProperty("confidence").GetString()));
        Assert.Equal(0, body.GetProperty("sources").GetArrayLength());
        Assert.Equal(DietaryAllergenAnalysisPolicy.CrossContactDisclaimer, body.GetProperty("crossContactDisclaimer").GetString());
    }

    // ---- Who may read, and what another workspace sees ----

    [Fact]
    public async Task A_viewer_may_read_an_analysis()
    {
        using var owner = await _fixture.SignInAsync(_fixture.WorkspaceB.OwnerEmail, cancellationToken: Ct);
        var recipeId = await CreateAsync(owner, _fixture.WorkspaceB, ("200 g flour", _reference.Flour));

        using var viewer = await _fixture.SignInAsync(_fixture.WorkspaceB.MemberEmail, cancellationToken: Ct);
        var response = await AnalyzeAsync(viewer, _fixture.WorkspaceB, recipeId, 1, new { allergenIds = new[] { _reference.Wheat } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Another_workspaces_recipe_is_not_found_and_a_non_member_cannot_reach_the_workspace()
    {
        using var ownerA = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);
        var recipeA = await CreateAsync(ownerA, _fixture.WorkspaceA, ("200 g flour", _reference.Flour));
        var selection = new { allergenIds = new[] { _reference.Wheat } };

        // B's owner, naming A's recipe under B's own workspace: it is not there.
        using var ownerB = await _fixture.SignInAsync(_fixture.WorkspaceB.OwnerEmail, cancellationToken: Ct);
        var underB = await AnalyzeAsync(ownerB, _fixture.WorkspaceB, recipeA, 1, selection);
        Assert.Equal(HttpStatusCode.NotFound, underB.StatusCode);
        Assert.Equal("recipes.recipe.not_found", (await BodyOf(underB)).GetProperty("code").GetString());

        // B's owner, naming A's workspace: not a member, so not found either.
        var underA = await AnalyzeAsync(ownerB, _fixture.WorkspaceA, recipeA, 1, selection);
        Assert.Equal(HttpStatusCode.NotFound, underA.StatusCode);
    }

    // ---- Refusals ----

    [Fact]
    public async Task An_unknown_recipe_or_version_number_is_not_found_and_says_which()
    {
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);
        var recipeId = await CreateAsync(client, _fixture.WorkspaceA, ("200 g flour", _reference.Flour));
        var selection = new { allergenIds = new[] { _reference.Wheat } };

        var unknownRecipe = await AnalyzeAsync(client, _fixture.WorkspaceA, Guid.NewGuid(), 1, selection);
        Assert.Equal(HttpStatusCode.NotFound, unknownRecipe.StatusCode);
        Assert.Equal("recipes.recipe.not_found", (await BodyOf(unknownRecipe)).GetProperty("code").GetString());

        var unknownVersion = await AnalyzeAsync(client, _fixture.WorkspaceA, recipeId, 9, selection);
        Assert.Equal(HttpStatusCode.NotFound, unknownVersion.StatusCode);
        var problem = await BodyOf(unknownVersion);
        Assert.Equal("recipes.version.not_found", problem.GetProperty("code").GetString());
        Assert.True(problem.GetProperty("errors").TryGetProperty("versionNumber", out _));
    }

    public static TheoryData<string> UnreadableSelections => new()
    {
        "{}",
        """{ "allergenIds": null, "dietaryProfileIds": null }""",
        """{ "allergenIds": ["00000000-0000-0000-0000-000000000000"] }""",
        """{ "allergenIds": ["11111111-1111-1111-1111-111111111111", "11111111-1111-1111-1111-111111111111"] }""",
        $$"""{ "allergenIds": [{{string.Join(", ", Enumerable.Range(1, 21).Select(i => $"\"{new Guid(i, 0, 0, new byte[8])}\""))}}] }""",
        $$"""{ "dietaryProfileIds": [{{string.Join(", ", Enumerable.Range(1, 21).Select(i => $"\"{new Guid(i, 0, 0, new byte[8])}\""))}}] }""",
        """{ "dietaryProfileIds": ["22222222-2222-2222-2222-222222222222", "22222222-2222-2222-2222-222222222222"] }""",
    };

    [Theory]
    [MemberData(nameof(UnreadableSelections))]
    public async Task A_selection_that_cannot_be_read_is_a_bad_request_with_field_errors(string json)
    {
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);
        var recipeId = await CreateAsync(client, _fixture.WorkspaceA, ("200 g flour", _reference.Flour));

        var response = await client.PostAsync(
            AnalysisRoute(_fixture.WorkspaceA, recipeId, 1), new StringContent(json, System.Text.Encoding.UTF8, "application/json"), cancellationToken: Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await BodyOf(response);
        Assert.Equal("nutrition.analysis.invalid", problem.GetProperty("code").GetString());
        Assert.True(problem.GetProperty("errors").EnumerateObject().Any());
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    public async Task A_missing_or_null_body_is_malformed_as_on_every_route(string json)
    {
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);
        var recipeId = await CreateAsync(client, _fixture.WorkspaceA, ("200 g flour", _reference.Flour));

        var response = await client.PostAsync(
            AnalysisRoute(_fixture.WorkspaceA, recipeId, 1), new StringContent(json, System.Text.Encoding.UTF8, "application/json"), cancellationToken: Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("request.malformed", (await BodyOf(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_dietary_profile_the_catalogue_does_not_offer_is_unprocessable()
    {
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);
        var recipeId = await CreateAsync(client, _fixture.WorkspaceA, ("200 g flour", _reference.Flour));

        // An allergen's id is not a profile's.
        var response = await AnalyzeAsync(client, _fixture.WorkspaceA, recipeId, 1, new { dietaryProfileIds = new[] { _reference.Wheat } });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await BodyOf(response);
        Assert.Equal("nutrition.analysis.subject.unprocessable", problem.GetProperty("code").GetString());
        Assert.True(problem.GetProperty("errors").TryGetProperty("dietaryProfileIds", out _));
    }

    [Fact]
    public async Task A_retired_or_unknown_allergen_is_unprocessable()
    {
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);
        var recipeId = await CreateAsync(client, _fixture.WorkspaceA, ("200 g flour", _reference.Flour));

        foreach (var allergen in new[] { _reference.RetiredAllergen, Guid.NewGuid() })
        {
            var response = await AnalyzeAsync(client, _fixture.WorkspaceA, recipeId, 1, new { allergenIds = new[] { allergen } });

            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal("nutrition.analysis.subject.unprocessable", (await BodyOf(response)).GetProperty("code").GetString());
        }
    }

    // ---- Helpers ----

    private sealed record Reference(
        Guid Wheat, Guid RetiredAllergen, Guid Vegan, Guid Flour, Guid Sugar, string SourceCode);

    private static string AnalysisRoute(SeededWorkspace workspace, Guid recipeId, int versionNumber) =>
        $"/api/v1/workspaces/{workspace.Slug}/recipes/{recipeId}/versions/{versionNumber}/analysis/dietary-allergen";

    private static Task<HttpResponseMessage> AnalyzeAsync(GatewayClient client, SeededWorkspace workspace, Guid recipeId, int versionNumber, object body) =>
        client.PostAsJsonAsync(AnalysisRoute(workspace, recipeId, versionNumber), body, Ct);

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    private static object Lines(params (string Text, Guid? IngredientId)[] lines) =>
        new[] { new { ingredients = lines.Select(line => new { displayText = line.Text, ingredientId = line.IngredientId }).ToArray() } };

    private static async Task<Guid> CreateAsync(GatewayClient client, SeededWorkspace workspace, params (string Text, Guid? IngredientId)[] lines)
    {
        var created = await client.PostAsJsonAsync(
            $"/api/v1/workspaces/{workspace.Slug}/recipes",
            new { title = "Sugar cookies", ingredientGroups = Lines(lines) },
            Ct);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        return (await BodyOf(await client.GetAsync(created.Headers.Location!.ToString(), Ct))).GetProperty("id").GetGuid();
    }

    /// <summary>An ordinary edit, which writes version 2.</summary>
    private static async Task ReplaceLinesAsync(GatewayClient client, SeededWorkspace workspace, Guid recipeId, params (string Text, Guid? IngredientId)[] lines)
    {
        var route = $"/api/v1/workspaces/{workspace.Slug}/recipes/{recipeId}";
        var detail = await BodyOf(await client.GetAsync(route, Ct));

        var response = await client.PatchAsJsonAsync(
            route,
            new { expectedConcurrencyToken = detail.GetProperty("concurrencyToken").GetString(), ingredientGroups = Lines(lines) },
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>Platform reference rows no other test uses: two allergens (one retired), a profile, two ingredients and a licensed source.</summary>
    private async Task<Reference> SeedReferenceAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var reference = new Reference(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), $"test-list-{suffix}");
        var source = Guid.NewGuid();

        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        db.Allergens.AddRange(
            new Allergen { Id = reference.Wheat, Code = $"wheat-{suffix}", DisplayName = "Wheat", Description = "Wheat." },
            new Allergen { Id = reference.RetiredAllergen, Code = $"lupin-{suffix}", DisplayName = "Lupin", Description = "Lupin.", IsActive = false });
        db.DietaryProfiles.Add(new DietaryProfile { Id = reference.Vegan, Code = $"vegan-{suffix}", DisplayName = "Vegan" });

        db.Ingredients.AddRange(
            new Ingredient { Id = reference.Flour, CanonicalName = $"flour {suffix}", NormalizedName = $"flour {suffix}", SearchText = $"flour {suffix}" },
            new Ingredient { Id = reference.Sugar, CanonicalName = $"sugar {suffix}", NormalizedName = $"sugar {suffix}", SearchText = $"sugar {suffix}" });

        db.ReferenceSources.Add(new ReferenceSource
        {
            Id = source,
            Code = reference.SourceCode,
            Name = "A test official list",
            Kind = ReferenceSourceKind.OfficialDatabase,
            Citation = "A test official list, 2037.",
            LicenseCode = "CC0-1.0",
            LicenseUrl = "https://creativecommons.org/publicdomain/zero/1.0/",
        });

        db.IngredientAllergenTraits.Add(new IngredientAllergenTrait
        {
            Id = Guid.NewGuid(),
            IngredientId = reference.Flour,
            AllergenId = reference.Wheat,
            ReferenceSourceId = source,
            ReferenceSourceKind = ReferenceSourceKind.OfficialDatabase,
            Presence = AllergenPresence.Present,
            EvidenceNote = "Stated by the source.",
            EffectiveFrom = new DateOnly(2025, 1, 1),
            ReviewStatus = TraitReviewStatus.Approved,
        });
        db.IngredientDietaryTraits.Add(new IngredientDietaryTrait
        {
            Id = Guid.NewGuid(),
            IngredientId = reference.Sugar,
            DietaryProfileId = reference.Vegan,
            ReferenceSourceId = source,
            ReferenceSourceKind = ReferenceSourceKind.OfficialDatabase,
            Compatibility = DietaryCompatibility.DependsOnProduct,
            EvidenceNote = "Some cane sugar is filtered through bone char.",
            EffectiveFrom = new DateOnly(2026, 2, 1),
            ReviewStatus = TraitReviewStatus.Approved,
        });

        await db.SaveChangesAsync(Ct);

        return reference;
    }
}

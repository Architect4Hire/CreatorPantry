using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using CreatorPantry.Tests.Gateway;
using CreatorPantry.Tests.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CreatorPantry.Tests.Recipes;

/// <summary>
/// <c>POST /api/v1/workspaces/{workspaceSlug}/ingredient-tools/parse</c> through the real Gateway — combining
/// the tokenizer (7.2) and the reference matchers (7.3) into one read-only proposal (ING-001, 7.4).
/// </summary>
/// <remarks>
/// Genuinely ambiguous matcher output (two different entities tied at the same precedence tier) cannot be
/// produced from the real seeded catalogue by design — <c>IngredientMatcherTests</c>/<c>UnitMatcherTests</c>
/// already prove that mechanism against a hand-built index. What this endpoint adds on top is the HTTP
/// contract itself: that an unresolved value reaches the response unhidden, that abusive input is clamped,
/// and that workspace isolation holds — those are what these tests cover.
/// </remarks>
public sealed class IngredientParseEndpointTests : IAsyncLifetime
{
    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync()
    {
        _fixture = await TwoWorkspaceGatewayFixture.CreateAsync();

        // The gateway/API test host does not run the deployment seeder, so the reference catalogue starts
        // empty — seed it directly the same way ReferenceCatalogFixture does, so "kosher salt"/"tsp" resolve
        // to real rows rather than to nothing.
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        await new ReferenceDataSeeder(
                context,
                new ReferenceSeedOptions(IncludeDevelopmentSampleData: true),
                NullLogger<ReferenceDataSeeder>.Instance)
            .SeedAsync(TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static string ParseUrl(SeededWorkspace workspace) =>
        $"/api/v1/workspaces/{workspace.Slug}/ingredient-tools/parse";

    // ---- Valid ----

    [Fact]
    public async Task A_line_with_an_exact_unit_and_ingredient_match_resolves_both()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);

        var body = await ParseAsync(client, _fixture.WorkspaceA, ["2 tsp kosher salt"]);
        var line = body.GetProperty("lines").EnumerateArray().Single();

        Assert.Equal("2 tsp kosher salt", line.GetProperty("tokens").GetProperty("originalText").GetString());
        Assert.Equal("2", line.GetProperty("tokens").GetProperty("quantity").GetProperty("span").GetProperty("text").GetString());

        var unitMatch = line.GetProperty("unitMatch");
        Assert.Equal("tsp", unitMatch.GetProperty("inputText").GetString());
        Assert.Equal("Code", unitMatch.GetProperty("resolved").GetProperty("kind").GetString());

        var ingredientMatch = line.GetProperty("ingredientMatch");
        Assert.Equal("kosher salt", ingredientMatch.GetProperty("inputText").GetString());
        Assert.Equal("kosher salt", ingredientMatch.GetProperty("resolved").GetProperty("canonicalName").GetString());
        Assert.Equal("CanonicalName", ingredientMatch.GetProperty("resolved").GetProperty("kind").GetString());
    }

    [Fact]
    public async Task An_alias_ingredient_resolves_through_the_real_catalogue()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);

        var body = await ParseAsync(client, _fixture.WorkspaceA, ["1 tbsp cornflour"]);
        var line = body.GetProperty("lines").EnumerateArray().Single();

        var ingredientMatch = line.GetProperty("ingredientMatch");
        Assert.Equal("cornstarch", ingredientMatch.GetProperty("resolved").GetProperty("canonicalName").GetString());
        Assert.Equal("Alias", ingredientMatch.GetProperty("resolved").GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Multiple_lines_are_parsed_in_order()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);

        var body = await ParseAsync(client, _fixture.WorkspaceA, ["2 tsp kosher salt", "1 tbsp cornflour"]);
        var lines = body.GetProperty("lines").EnumerateArray().ToList();

        Assert.Equal(2, lines.Count);
        Assert.Equal("2 tsp kosher salt", lines[0].GetProperty("tokens").GetProperty("originalText").GetString());
        Assert.Equal("1 tbsp cornflour", lines[1].GetProperty("tokens").GetProperty("originalText").GetString());
    }

    [Fact]
    public async Task A_group_marker_line_carries_no_matches()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);

        var body = await ParseAsync(client, _fixture.WorkspaceA, ["For the crust:"]);
        var line = body.GetProperty("lines").EnumerateArray().Single();

        Assert.True(line.GetProperty("tokens").GetProperty("isGroupMarker").GetBoolean());
        Assert.Equal(JsonValueKind.Null, line.GetProperty("ingredientMatch").ValueKind);
        Assert.Equal(JsonValueKind.Null, line.GetProperty("unitMatch").ValueKind);
    }

    // ---- Ambiguous / unresolved ----

    [Fact]
    public async Task An_unresolved_quantity_and_an_unresolved_ingredient_are_not_hidden()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);

        var body = await ParseAsync(client, _fixture.WorkspaceA, ["salt to taste"]);
        var line = body.GetProperty("lines").EnumerateArray().Single();

        Assert.Equal(JsonValueKind.Null, line.GetProperty("tokens").GetProperty("quantity").ValueKind);

        var ambiguity = line.GetProperty("tokens").GetProperty("ambiguities").EnumerateArray().Single();
        Assert.Equal("NoQuantityDetected", ambiguity.GetProperty("kind").GetString());

        // No catalogue entry reads "salt to taste" — the unresolved match is still present in the body,
        // not omitted because it found nothing.
        var ingredientMatch = line.GetProperty("ingredientMatch");
        Assert.Equal(JsonValueKind.Null, ingredientMatch.GetProperty("resolved").ValueKind);
        Assert.False(ingredientMatch.GetProperty("isAmbiguous").GetBoolean());
    }

    [Fact]
    public async Task An_unverified_unit_candidate_is_returned_even_when_it_does_not_resolve()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);

        // "large" is captured as a unit candidate by the tokenizer (7.2) purely by position; the real
        // catalogue has no such unit, so it comes back unresolved rather than silently dropped.
        var body = await ParseAsync(client, _fixture.WorkspaceA, ["1-2 large eggs"]);
        var line = body.GetProperty("lines").EnumerateArray().Single();

        Assert.Equal("large", line.GetProperty("tokens").GetProperty("unitCandidate").GetProperty("text").GetString());
        Assert.Equal(JsonValueKind.Null, line.GetProperty("unitMatch").GetProperty("resolved").ValueKind);
    }

    // ---- Abusive ----

    [Fact]
    public async Task Too_many_lines_are_refused()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);

        var lines = Enumerable.Range(0, IngredientParsingPolicy.MaxLines + 1).Select(i => $"{i} eggs").ToArray();
        var response = await client.PostAsJsonAsync(ParseUrl(_fixture.WorkspaceA), new { lines }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            IngredientParsingErrorCodes.LinesInvalidRequest,
            (await ReadAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task An_over_long_line_is_refused()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);

        var response = await client.PostAsJsonAsync(
            ParseUrl(_fixture.WorkspaceA),
            new { lines = new[] { new string('a', IngredientParsingPolicy.MaxLineLength + 1) } },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            IngredientParsingErrorCodes.LinesInvalidRequest,
            (await ReadAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_blank_line_is_refused()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);

        var response = await client.PostAsJsonAsync(
            ParseUrl(_fixture.WorkspaceA), new { lines = new[] { "2 cups flour", "   " } }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            IngredientParsingErrorCodes.LinesInvalidRequest,
            (await ReadAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task An_empty_line_list_is_refused()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);

        var response = await client.PostAsJsonAsync(
            ParseUrl(_fixture.WorkspaceA), new { lines = Array.Empty<string>() }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- Cross-workspace ----

    [Fact]
    public async Task A_member_of_one_workspace_cannot_reach_the_route_under_another_workspaces_slug()
    {
        using var ownerB = await SignInAsync(_fixture.WorkspaceB);

        // B's owner names A's slug: 404, not a validation error and not 403, so the reply does not confirm
        // that Workspace A exists (tenancy.md) — the workspace-resolution middleware refuses this before the
        // controller action, and every other workspace route relies on the same refusal without special-casing.
        var response = await ownerB.PostAsJsonAsync(
            ParseUrl(_fixture.WorkspaceA), new { lines = new[] { "2 cups flour" } }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- Helpers ----

    private Task<GatewayClient> SignInAsync(SeededWorkspace workspace, bool asOwner = true) =>
        _fixture.SignInAsync(
            asOwner ? workspace.OwnerEmail : workspace.MemberEmail,
            cancellationToken: TestContext.Current.CancellationToken);

    private static async Task<JsonElement> ParseAsync(GatewayClient client, SeededWorkspace workspace, IReadOnlyList<string> lines)
    {
        var response = await client.PostAsJsonAsync(ParseUrl(workspace), new { lines }, TestContext.Current.CancellationToken);
        var body = await ReadAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return body;
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
}

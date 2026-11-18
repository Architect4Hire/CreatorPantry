using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Modules.Measurement.Seeding;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using CreatorPantry.Tests.Gateway;
using CreatorPantry.Tests.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CreatorPantry.Tests.Recipes;

/// <summary>
/// <c>POST …/ingredient-tools/match-units</c>: whether each unit word is one an ingredient line would record as a
/// CreatorPantry unit — the question the draft review screen asks before a creator accepts a generated draft, and
/// the one accepting it answers with the same resolver.
/// </summary>
public sealed class IngredientUnitMatchEndpointTests : IAsyncLifetime
{
    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync()
    {
        _fixture = await TwoWorkspaceGatewayFixture.CreateAsync();

        // The gateway/API host does not run the deployment seeder, so the real unit catalogue is seeded here, the
        // way IngredientParseEndpointTests does it — these words have to resolve against real rows and aliases.
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        await new ReferenceDataSeeder(
                context,
                new ReferenceSeedOptions(IncludeDevelopmentSampleData: true),
                NullLogger<ReferenceDataSeeder>.Instance)
            .SeedAsync(TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static string MatchUrl(SeededWorkspace workspace) =>
        $"/api/v1/workspaces/{workspace.Slug}/ingredient-tools/match-units";

    [Fact]
    public async Task Each_word_comes_back_in_order_with_the_unit_it_would_be_recorded_as()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);

        var words = await MatchAsync(client, _fixture.WorkspaceA, ["tbsp", " Tablespoons ", "cloves", "knob"]);

        Assert.Equal(["tbsp", " Tablespoons ", "cloves", "knob"], words.Select(word => word.GetProperty("word").GetString()));
        Assert.Equal(MeasurementSeedData.UnitId("tbsp"), words[0].GetProperty("measurementUnitId").GetGuid());
        Assert.Equal(MeasurementSeedData.UnitId("tbsp"), words[1].GetProperty("measurementUnitId").GetGuid());
        Assert.Equal(MeasurementSeedData.UnitId("clove"), words[2].GetProperty("measurementUnitId").GetGuid());
        Assert.Equal(JsonValueKind.Null, words[3].GetProperty("measurementUnitId").ValueKind);
    }

    /// <summary>
    /// "large" is a size, not a unit, and "fahrenheit" is a real unit no ingredient may be measured in. Both are
    /// words a draft would be accepted holding with no unit, so both come back with none — the screen flags them.
    /// </summary>
    [Fact]
    public async Task A_size_word_and_a_temperature_unit_are_not_units_an_ingredient_records()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);

        var words = await MatchAsync(client, _fixture.WorkspaceA, ["large", "fahrenheit"]);

        Assert.All(words, word => Assert.Equal(JsonValueKind.Null, word.GetProperty("measurementUnitId").ValueKind));
    }

    /// <summary>It reads reference data and changes nothing, so a viewer may ask it, as they may parse.</summary>
    [Fact]
    public async Task A_member_who_is_not_the_owner_may_ask()
    {
        using var member = await SignInAsync(_fixture.WorkspaceA, asOwner: false);

        var response = await member.PostAsJsonAsync(
            MatchUrl(_fixture.WorkspaceA), new { words = new[] { "cup" } }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(IngredientParsingPolicy.MaxLines + 1)]
    public async Task No_words_or_too_many_are_refused(int count)
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);

        var words = Enumerable.Range(0, count).Select(index => $"unit{index}").ToArray();
        var response = await client.PostAsJsonAsync(
            MatchUrl(_fixture.WorkspaceA), new { words }, TestContext.Current.CancellationToken);

        await AssertRefusedAsync(response);
    }

    [Fact]
    public async Task A_blank_or_over_long_word_is_refused()
    {
        using var client = await SignInAsync(_fixture.WorkspaceA);

        foreach (var bad in new[] { "   ", new string('a', IngredientParsingPolicy.MaxLineLength + 1) })
        {
            var response = await client.PostAsJsonAsync(
                MatchUrl(_fixture.WorkspaceA), new { words = new[] { "cup", bad } }, TestContext.Current.CancellationToken);

            await AssertRefusedAsync(response);
        }
    }

    /// <summary>404, not 403: the reply does not confirm that another workspace exists (tenancy.md).</summary>
    [Fact]
    public async Task A_member_of_one_workspace_cannot_reach_the_route_under_another_workspaces_slug()
    {
        using var ownerB = await SignInAsync(_fixture.WorkspaceB);

        var response = await ownerB.PostAsJsonAsync(
            MatchUrl(_fixture.WorkspaceA), new { words = new[] { "cup" } }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private Task<GatewayClient> SignInAsync(SeededWorkspace workspace, bool asOwner = true) =>
        _fixture.SignInAsync(
            asOwner ? workspace.OwnerEmail : workspace.MemberEmail,
            cancellationToken: TestContext.Current.CancellationToken);

    private static async Task<List<JsonElement>> MatchAsync(
        GatewayClient client, SeededWorkspace workspace, IReadOnlyList<string> words)
    {
        var response = await client.PostAsJsonAsync(MatchUrl(workspace), new { words }, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return [.. body.GetProperty("words").EnumerateArray()];
    }

    private static async Task AssertRefusedAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(IngredientParsingErrorCodes.UnitWordsInvalidRequest, body.GetProperty("code").GetString());
    }
}

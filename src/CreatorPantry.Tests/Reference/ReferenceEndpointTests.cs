extern alias ApiService;

using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Outbox;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Auth.Data.Entities;
using CreatorPantry.Domain.Modules.Measurement.Data.Entities;
using CreatorPantry.Domain.Modules.Vocabulary.Data.Entities;
using CreatorPantry.Domain.Modules.Ingredients.Data.Entities;
using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Modules.Measurement.Seeding;
using CreatorPantry.Domain.Modules.Vocabulary.Seeding;
using CreatorPantry.Domain.Modules.Ingredients.Seeding;
using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.ServiceDefaults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace CreatorPantry.Tests.Reference;

/// <summary>
/// The reference routes over the real <c>Program.cs</c> pipeline — real authentication, real authorization,
/// real EF, real cache registration — against the real seeded catalogue.
/// </summary>
public sealed class ReferenceEndpointTests : IAsyncLifetime
{
    private SqliteApiHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await SqliteApiHost.StartAsync();

        await using var scope = _host.Factory.Services.CreateAsyncScope();
        await new ReferenceDataSeeder(
                scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>(),
                new ReferenceSeedOptions(IncludeDevelopmentSampleData: true),
                NullLogger<ReferenceDataSeeder>.Instance)
            .SeedAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    // --- The contract ------------------------------------------------------------------------------------

    [Theory]
    [InlineData("ingredients")]
    [InlineData("units")]
    [InlineData("food-categories")]
    [InlineData("cuisines")]
    [InlineData("courses")]
    [InlineData("techniques")]
    [InlineData("equipment-types")]
    [InlineData("dietary-profiles")]
    [InlineData("allergens")]
    public async Task Every_resource_returns_the_same_page_envelope(string resource)
    {
        var response = await GetAsync($"/api/v1/reference/{resource}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadAsync(response);
        Assert.Equal(JsonValueKind.Array, body.GetProperty("items").ValueKind);
        Assert.True(body.TryGetProperty("nextCursor", out _));
        Assert.NotEmpty(body.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task A_cuisine_carries_its_id_code_and_display_name()
    {
        var body = await ReadAsync(await GetAsync("/api/v1/reference/cuisines?search=italian"));

        var only = Assert.Single(body.GetProperty("items").EnumerateArray());
        Assert.Equal("italian", only.GetProperty("code").GetString());
        Assert.Equal("Italian", only.GetProperty("displayName").GetString());
        Assert.NotEqual(Guid.Empty, only.GetProperty("id").GetGuid());
    }

    /// <summary>Retired entries never appear, so the flag that would say so is not in the payload either.</summary>
    [Fact]
    public async Task Responses_do_not_carry_an_active_flag()
    {
        var body = await ReadAsync(await GetAsync("/api/v1/reference/cuisines"));

        Assert.All(
            body.GetProperty("items").EnumerateArray(),
            item => Assert.False(item.TryGetProperty("isActive", out _)));
    }

    /// <summary>
    /// Enums go out as their declared names, not their numbers. Without this the contract is asymmetric — the
    /// dimension filter accepts "Temperature" while the response would answer 3 — and a client has to keep a
    /// private integer-to-name mapping the document never gave it. Pinned here so the representation cannot
    /// drift back before a client depends on it.
    /// </summary>
    [Fact]
    public async Task Enums_are_serialized_by_name()
    {
        var body = await ReadAsync(await GetAsync("/api/v1/reference/units?dimension=Temperature&limit=100"));

        var items = body.GetProperty("items").EnumerateArray().ToList();
        Assert.NotEmpty(items);
        Assert.All(items, item =>
        {
            Assert.Equal(JsonValueKind.String, item.GetProperty("dimension").ValueKind);
            Assert.Equal(nameof(MeasurementDimension.Temperature), item.GetProperty("dimension").GetString());
            Assert.Equal(JsonValueKind.String, item.GetProperty("system").ValueKind);
        });
    }

    /// <summary>A filter value read from a response must be usable as a filter, unchanged.</summary>
    [Fact]
    public async Task A_dimension_read_from_a_response_can_be_sent_back_as_a_filter()
    {
        var first = await ReadAsync(await GetAsync("/api/v1/reference/units?limit=1"));
        var dimension = first.GetProperty("items").EnumerateArray().Single().GetProperty("dimension").GetString();

        var response = await GetAsync($"/api/v1/reference/units?dimension={dimension}&limit=100");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.All(
            (await ReadAsync(response)).GetProperty("items").EnumerateArray(),
            item => Assert.Equal(dimension, item.GetProperty("dimension").GetString()));
    }

    [Fact]
    public async Task A_technique_carries_the_safety_caution_flag()
    {
        var body = await ReadAsync(await GetAsync("/api/v1/reference/techniques?search=canning&limit=100"));

        var canning = body.GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("code").GetString() == "water-bath-canning");

        Assert.True(canning.GetProperty("requiresSafetyCaution").GetBoolean());
    }

    [Fact]
    public async Task An_ingredient_is_searchable_by_alias_over_http()
    {
        var body = await ReadAsync(await GetAsync("/api/v1/reference/ingredients?search=scallion"));

        var only = Assert.Single(body.GetProperty("items").EnumerateArray());
        Assert.Equal("green onion", only.GetProperty("canonicalName").GetString());
        Assert.Contains("scallion", only.GetProperty("aliases").EnumerateArray().Select(alias => alias.GetString()));
    }

    // --- Paging ------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_page_can_be_followed_to_the_end_over_http()
    {
        var seen = new List<string>();
        string? cursor = null;

        for (var safety = 0; safety < 100; safety++)
        {
            var body = await ReadAsync(await GetAsync(
                $"/api/v1/reference/cuisines?limit=3{(cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}")}"));

            seen.AddRange(body.GetProperty("items").EnumerateArray()
                .Select(item => item.GetProperty("code").GetString()!));

            cursor = body.GetProperty("nextCursor").GetString();
            if (cursor is null)
            {
                break;
            }
        }

        Assert.Equal(CatalogueSeedData.Cuisines().Count, seen.Count);
        Assert.Equal(seen.Distinct().Count(), seen.Count);
    }

    /// <summary>The clamp in action: asking for far more than the maximum succeeds and returns the maximum.</summary>
    [Fact]
    public async Task An_over_large_page_size_is_clamped_rather_than_rejected()
    {
        var response = await GetAsync("/api/v1/reference/units?limit=100000");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = (await ReadAsync(response)).GetProperty("items").EnumerateArray().Count();
        Assert.True(items <= ReferencePolicy.MaxPageSize);
        Assert.Equal(Math.Min(MeasurementSeedData.Units().Count, ReferencePolicy.MaxPageSize), items);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-4)]
    public async Task A_non_positive_page_size_is_clamped_to_one(int limit)
    {
        var response = await GetAsync($"/api/v1/reference/cuisines?limit={limit}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single((await ReadAsync(response)).GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task An_absent_page_size_uses_the_documented_default()
    {
        var body = await ReadAsync(await GetAsync("/api/v1/reference/techniques"));

        Assert.Equal(
            ReferencePolicy.DefaultPageSize,
            body.GetProperty("items").EnumerateArray().Count());
    }

    // --- Cache correctness -------------------------------------------------------------------------------

    /// <summary>
    /// The regression the reviewers caught, through the real pipeline where a fake business cannot hide it.
    /// <c>gluten-free</c> and <c>gluten free</c> normalize to one key, but only the hyphenated spelling is a
    /// substring of the display name "Gluten-Free". When the cache keyed on the normalized form alone, the
    /// first of these two requests decided the answer to the second for ten minutes — and the empty page it
    /// served reads as "no such dietary profile", which is a cache artifact presented as a catalogue fact.
    /// Order matters, so the miss is issued first.
    /// </summary>
    [Fact]
    public async Task Two_spellings_that_match_different_rows_do_not_share_a_cached_answer()
    {
        var spaced = await ReadAsync(await GetAsync("/api/v1/reference/dietary-profiles?search=gluten%20free"));
        Assert.Empty(spaced.GetProperty("items").EnumerateArray());

        var hyphenated = await ReadAsync(await GetAsync("/api/v1/reference/dietary-profiles?search=gluten-free"));

        var only = Assert.Single(hyphenated.GetProperty("items").EnumerateArray());
        Assert.Equal("gluten-free", only.GetProperty("code").GetString());
    }

    /// <summary>The same collision on a vocabulary that does have an alias table.</summary>
    [Fact]
    public async Task A_hyphenated_cuisine_is_not_masked_by_its_spaced_spelling()
    {
        await GetAsync("/api/v1/reference/cuisines?search=tex%20mex");

        var body = await ReadAsync(await GetAsync("/api/v1/reference/cuisines?search=tex-mex"));

        Assert.Equal("tex-mex", Assert.Single(body.GetProperty("items").EnumerateArray()).GetProperty("code").GetString());
    }

    /// <summary>
    /// A served page must be the page that was asked for. Reading the same query twice exercises the miss and
    /// then the hit, and both must agree.
    /// </summary>
    [Fact]
    public async Task A_cached_page_matches_the_page_that_produced_it()
    {
        var first = await (await GetAsync("/api/v1/reference/techniques?search=canning")).Content
            .ReadAsStringAsync(TestContext.Current.CancellationToken);
        var second = await (await GetAsync("/api/v1/reference/techniques?search=canning")).Content
            .ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(first, second);
        Assert.Contains("water-bath-canning", first, StringComparison.Ordinal);
    }

    /// <summary>
    /// Reference data does not vary by caller, so two members of two different workspaces must receive the
    /// identical body. This is the guard that fails loudly if anyone later threads an IWorkspaceContext into
    /// this seam or hand-builds a workspace-scoped key.
    /// </summary>
    [Fact]
    public async Task Members_of_different_workspaces_receive_the_identical_catalogue()
    {
        var first = await _host.CreateUserAsync("one@example.com", "correct horse battery");
        var second = await _host.CreateUserAsync("two@example.com", "correct horse battery");

        var forFirst = await (await GetAsync("/api/v1/reference/cuisines?limit=100", first)).Content
            .ReadAsStringAsync(TestContext.Current.CancellationToken);
        var forSecond = await (await GetAsync("/api/v1/reference/cuisines?limit=100", second)).Content
            .ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(forFirst, forSecond);
    }

    // --- Errors ------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_corrupt_cursor_is_a_problem_details_with_a_stable_code()
    {
        var response = await GetAsync("/api/v1/reference/cuisines?cursor=nonsense!!");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var body = await ReadAsync(response);
        Assert.Equal(ReferenceErrorCodes.CursorInvalid, body.GetProperty("code").GetString());
        Assert.True(body.GetProperty("errors").TryGetProperty("cursor", out _));
    }

    /// <summary>
    /// A real cursor from one resource, replayed against another. Before it carried its query's fingerprint
    /// this decoded fine and returned a plausible page from the wrong position in the wrong set.
    /// </summary>
    [Fact]
    public async Task A_cursor_from_another_resource_is_refused_over_http()
    {
        var cuisines = await ReadAsync(await GetAsync("/api/v1/reference/cuisines?limit=2"));
        var cursor = cuisines.GetProperty("nextCursor").GetString();
        Assert.NotNull(cursor);

        var response = await GetAsync($"/api/v1/reference/allergens?cursor={Uri.EscapeDataString(cursor)}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await ReadAsync(response);
        Assert.Equal(ReferenceErrorCodes.CursorInvalid, body.GetProperty("code").GetString());
    }

    /// <summary>The likelier mistake: a client keeps paging after changing the filter.</summary>
    [Fact]
    public async Task A_cursor_is_refused_once_the_search_changes_over_http()
    {
        var page = await ReadAsync(await GetAsync("/api/v1/reference/ingredients?search=flour&limit=1"));
        var cursor = page.GetProperty("nextCursor").GetString();
        Assert.NotNull(cursor);

        var escaped = Uri.EscapeDataString(cursor);
        var sameSearch = await GetAsync($"/api/v1/reference/ingredients?search=flour&cursor={escaped}");
        var changedSearch = await GetAsync($"/api/v1/reference/ingredients?search=sugar&cursor={escaped}");

        Assert.Equal(HttpStatusCode.OK, sameSearch.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, changedSearch.StatusCode);
    }

    /// <summary>
    /// Includes the forms <c>Enum.TryParse</c> would have accepted: a numeric value that is not a declared
    /// member, and a comma-delimited combination. Both previously returned an empty 200.
    /// </summary>
    [Theory]
    [InlineData("furlongs")]
    [InlineData("99")]
    [InlineData("3")]
    [InlineData("Mass,Volume")]
    public async Task A_dimension_outside_the_published_set_is_a_problem_details(string dimension)
    {
        var response = await GetAsync($"/api/v1/reference/units?dimension={Uri.EscapeDataString(dimension)}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await ReadAsync(response);
        Assert.Equal(ReferenceErrorCodes.QueryInvalid, body.GetProperty("code").GetString());
        Assert.True(body.GetProperty("errors").TryGetProperty("dimension", out _));
    }

    // --- Authorization and data zone ---------------------------------------------------------------------

    [Fact]
    public async Task An_anonymous_caller_is_refused()
    {
        using var client = _host.Factory.CreateClient();

        var response = await client.GetAsync("/api/v1/reference/cuisines", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// The data-zone split, proven at the edge: a signed-in caller who belongs to no workspace at all can read
    /// the shared catalogue. Nothing here resolves a workspace, and nothing here needs one.
    /// </summary>
    [Fact]
    public async Task A_caller_with_no_workspace_can_read_the_catalogue()
    {
        var userId = await _host.CreateUserAsync("nomad@example.com", "correct horse battery");

        var response = await GetAsync("/api/v1/reference/allergens", userId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEmpty((await ReadAsync(response)).GetProperty("items").EnumerateArray());
    }

    // --- Harness -----------------------------------------------------------------------------------------

    private async Task<HttpResponseMessage> GetAsync(string path, string? userId = null)
    {
        using var client = _host.Factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", CreateUserToken(userId ?? Guid.NewGuid().ToString()));

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

    /// <summary>Mints a real ES256 internal user token, the same shape the gateway would sign (baseline B-13).</summary>
    private static string CreateUserToken(string userId)
    {
        // Not disposed: IdentityModel caches signature providers per key, and a disposed key poisons that cache.
        var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(TestKeyPair.Shared.PrivateKeyPem);
        var now = DateTime.UtcNow;

        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = InternalTokenDefaults.Issuer,
            Audience = InternalTokenDefaults.Audience,
            Subject = new ClaimsIdentity(
            [
                new Claim(JwtRegisteredClaimNames.Sub, userId),
                new Claim(InternalTokenDefaults.TokenUseClaim, InternalTokenDefaults.UserTokenUse),
            ]),
            NotBefore = now,
            IssuedAt = now,
            Expires = now.AddMinutes(1),
            SigningCredentials = new SigningCredentials(new ECDsaSecurityKey(ecdsa), SecurityAlgorithms.EcdsaSha256),
        });
    }
}

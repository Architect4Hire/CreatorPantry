using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Tests.Tenancy;

namespace CreatorPantry.Tests.Brand;

public sealed class ContentChannelCatalogTests
{
    [Fact]
    public void The_default_catalogue_has_eight_unique_lowercase_keys()
    {
        var catalog = new ContentChannelCatalog();

        Assert.Equal(8, catalog.All.Count);
        Assert.Equal(catalog.All.Count, catalog.All.Select(channel => channel.Key).Distinct(StringComparer.Ordinal).Count());
        Assert.All(catalog.All, channel =>
        {
            Assert.Equal(channel.Key, channel.Key.ToLowerInvariant());
            Assert.False(string.IsNullOrWhiteSpace(channel.DisplayName));
            Assert.True(channel.IsActive);
        });
    }

    [Fact]
    public void Find_is_exact_and_returns_null_for_an_unknown_key()
    {
        var catalog = new ContentChannelCatalog();

        Assert.Equal("Instagram", catalog.Find("instagram")?.DisplayName);
        Assert.Equal("Newsletter", catalog.Find("newsletter")?.DisplayName);
        Assert.Null(catalog.Find("Instagram"));
        Assert.Null(catalog.Find("myspace"));
    }

    [Fact]
    public void A_catalogue_with_a_repeated_key_is_refused()
    {
        Assert.Throws<ArgumentException>(() => new ContentChannelCatalog(
            [new ContentChannel("a", "A"), new ContentChannel("a", "Again")]));
    }
}

public sealed class ContentChannelEndpointTests : IAsyncLifetime
{
    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync() => _fixture = await TwoWorkspaceGatewayFixture.CreateAsync();

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    [Fact]
    public async Task A_signed_in_member_reads_the_channels_in_display_order()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var client = await _fixture.SignInAsync(_fixture.WorkspaceB.MemberEmail, cancellationToken: cancellation);

        var response = await client.GetAsync("/api/v1/reference/content-channels", cancellation);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellation);
        Assert.Equal(
            ["blog", "newsletter", "instagram", "tiktok", "pinterest", "facebook", "x", "threads"],
            body.EnumerateArray().Select(channel => channel.GetProperty("key").GetString()));
        Assert.All(body.EnumerateArray(), channel => Assert.True(channel.GetProperty("isActive").GetBoolean()));
    }
}

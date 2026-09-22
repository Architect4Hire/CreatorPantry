extern alias Web;

using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Options;

namespace CreatorPantry.Tests.Web;

public sealed class WebHostTests : IDisposable
{
    private const string GatewayUrl = "https://gateway.test";
    private const string IndexHtml = "<!doctype html><cp-root></cp-root>";

    private readonly string _webRoot = Directory.CreateTempSubdirectory("cp-webroot-").FullName;

    public WebHostTests() => File.WriteAllText(Path.Combine(_webRoot, "index.html"), IndexHtml);

    public void Dispose() => Directory.Delete(_webRoot, recursive: true);

    [Fact]
    public async Task Runtime_config_returns_the_configured_gateway_url_uncached()
    {
        using var client = CreateFactory(GatewayUrl).CreateClient();

        var response = await client.GetAsync("/runtime-config.json", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(GatewayUrl, body!["gatewayUrl"]);
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/relative")]
    [InlineData("ftp://gateway.test")]
    public void Startup_fails_without_an_absolute_http_gateway_url(string? gatewayUrl)
    {
        using var factory = CreateFactory(gatewayUrl);

        Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
    }

    [Fact]
    public async Task Deep_links_fall_back_to_index_html()
    {
        using var client = CreateFactory(GatewayUrl).CreateClient();

        var response = await client.GetAsync("/recipes/42/edit", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(IndexHtml, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.True(response.Headers.CacheControl?.NoCache);
    }

    [Theory]
    [InlineData("/runtime-config.json", "application/json")]
    [InlineData("/health", "text/plain")]
    public async Task Fallback_does_not_capture_mapped_endpoints(string path, string mediaType)
    {
        using var client = CreateFactory(GatewayUrl).CreateClient();

        var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(mediaType, response.Content.Headers.ContentType?.MediaType);
    }

    private WebApplicationFactory<Web::Program> CreateFactory(string? gatewayUrl) =>
        new WebApplicationFactory<Web::Program>().WithWebHostBuilder(web =>
        {
            web.UseWebRoot(_webRoot);
            if (gatewayUrl is not null)
            {
                web.UseSetting("Client:GatewayUrl", gatewayUrl);
            }
        });
}

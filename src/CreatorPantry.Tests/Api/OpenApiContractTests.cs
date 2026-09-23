extern alias ApiService;

using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using ApiService::CreatorPantry.ApiService.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CreatorPantry.Tests.Api;

/// <summary>
/// The generated v1 OpenAPI document is a reviewed contract. To accept an intentional change, run the tests
/// with <c>UPDATE_OPENAPI_SNAPSHOT=1</c> and review the diff of <c>Snapshots/openapi-v1.json</c>.
/// </summary>
public sealed class OpenApiContractTests : IDisposable
{
    private readonly WebApplicationFactory<ApiService::Program> _factory =
        new WebApplicationFactory<ApiService::Program>().WithWebHostBuilder(TestDatabase.ConfigureWithoutHealthCheck);

    public void Dispose() => _factory.Dispose();

    /// <summary>
    /// Enum schemas name their members. A bare <c>"type": "integer"</c> publishes no names at all, so a
    /// generated client gets an <c>int</c> and has to carry a private mapping — and inserting a member
    /// becomes a silent renumbering rather than a visible change.
    /// </summary>
    [Theory]
    [InlineData("MeasurementDimension", "Temperature")]
    [InlineData("MeasurementSystem", "UsCustomary")]
    [InlineData("WorkspaceRole", "Owner")]
    [InlineData("WorkspaceMembershipStatus", "Active")]
    public async Task Enum_schemas_publish_their_member_names(string schema, string member)
    {
        var document = JsonNode.Parse(await GetDocumentTextAsync())!;
        var definition = document["components"]!["schemas"]![schema]
            ?? throw new InvalidOperationException($"No {schema} schema in the document.");

        Assert.Equal("string", definition["type"]!.GetValue<string>());
        Assert.Contains(
            member,
            definition["enum"]!.AsArray().Select(value => value!.GetValue<string>()));
    }

    [Fact]
    public async Task V1_document_matches_the_reviewed_snapshot()
    {
        var actual = Normalize(await GetDocumentTextAsync());
        var snapshotPath = SnapshotPath();

        if (Environment.GetEnvironmentVariable("UPDATE_OPENAPI_SNAPSHOT") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(snapshotPath)!);
            await File.WriteAllTextAsync(snapshotPath, actual, TestContext.Current.CancellationToken);
        }

        Assert.True(File.Exists(snapshotPath), $"No snapshot at {snapshotPath}; run with UPDATE_OPENAPI_SNAPSHOT=1.");
        var expected = Normalize(await File.ReadAllTextAsync(snapshotPath, TestContext.Current.CancellationToken));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task Every_operation_is_versioned_and_returns_problem_details_by_default()
    {
        var document = await GetDocumentAsync();
        var paths = document["paths"]!.AsObject();

        Assert.NotEmpty(paths);
        Assert.All(paths, path =>
        {
            Assert.StartsWith("/api/v1/", path.Key);
            Assert.All(path.Value!.AsObject(), operation =>
            {
                var problem = operation.Value!["responses"]!["default"]!["content"]!["application/problem+json"]!["schema"]!;
                Assert.Equal("#/components/schemas/ProblemDetails", problem["$ref"]!.GetValue<string>());

                // The gateway requires an antiforgery token on every unsafe request; the contract must say so.
                if (operation.Key is not ("get" or "head" or "options"))
                {
                    var parameters = operation.Value["parameters"]?.AsArray() ?? [];
                    Assert.Contains(parameters, parameter =>
                        parameter?["$ref"]?.GetValue<string>() == "#/components/parameters/AntiforgeryToken");
                }
            });
        });
    }

    [Fact]
    public async Task Anonymous_auth_operations_carry_no_security_requirement()
    {
        var document = await GetDocumentAsync();

        foreach (var path in new[] { "/api/v1/auth/register", "/api/v1/auth/password-reset", "/api/v1/auth/password-reset/complete" })
        {
            Assert.Null(document["paths"]![path]!["post"]!["security"]);
        }

        Assert.Null(document["security"]); // no document-wide requirement that would override anonymous routes
    }

    [Fact]
    public async Task Session_scheme_is_a_cookie_and_no_bearer_or_example_secrets_are_published()
    {
        var text = await GetDocumentTextAsync();
        var document = JsonNode.Parse(text)!;

        var schemes = document["components"]!["securitySchemes"]!.AsObject();
        var session = Assert.Single(schemes).Value!;
        Assert.Equal("apiKey", session["type"]!.GetValue<string>());
        Assert.Equal("cookie", session["in"]!.GetValue<string>());

        Assert.DoesNotContain("bearer", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"example", text, StringComparison.Ordinal);
        Assert.Null(document["servers"]);
    }

    [Fact]
    public async Task Shared_conventions_are_published_as_components()
    {
        var components = (await GetDocumentAsync())["components"]!;

        Assert.NotNull(components["schemas"]!["ProblemDetails"]!["properties"]!["code"]);
        Assert.NotNull(components["schemas"]!["ValidationProblemDetails"]!["properties"]!["errors"]);
        Assert.NotNull(components["schemas"]!["CursorPage"]!["properties"]!["nextCursor"]);
        Assert.Equal("Idempotency-Key", components["parameters"]!["IdempotencyKey"]!["name"]!.GetValue<string>());
        Assert.Equal("cursor", components["parameters"]!["Cursor"]!["name"]!.GetValue<string>());
        Assert.Equal("limit", components["parameters"]!["Limit"]!["name"]!.GetValue<string>());
        Assert.NotNull(components["requestBodies"]!["FileUpload"]!["content"]!["multipart/form-data"]);
    }

    [Fact]
    public async Task Scalar_reference_is_served_in_development()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/scalar", TestContext.Current.CancellationToken);
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // Scalar's script ships inside the package; the reference page does not load code from a CDN.
        var scripts = System.Text.RegularExpressions.Regex.Matches(html, "src=\"([^\"]+)\"").Select(match => match.Groups[1].Value).ToList();
        Assert.NotEmpty(scripts);
        Assert.All(scripts, source => Assert.False(Uri.TryCreate(source, UriKind.Absolute, out _), $"External script: {source}"));
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void Documentation_endpoints_are_not_mapped_outside_development(string environment)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.Services.AddCreatorPantryApiVersioning();
        var app = builder.Build();

        app.MapApiDocumentation();

        var routes = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText ?? string.Empty);
        Assert.DoesNotContain(routes, route => route.Contains("openapi", StringComparison.OrdinalIgnoreCase)
            || route.Contains("scalar", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<string> GetDocumentTextAsync()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }

    private async Task<JsonNode> GetDocumentAsync() => JsonNode.Parse(await GetDocumentTextAsync())!;

    private static string Normalize(string json) =>
        JsonNode.Parse(json)!.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).ReplaceLineEndings("\n") + "\n";

    private static string SnapshotPath([CallerFilePath] string sourceFile = "") =>
        Path.Combine(Path.GetDirectoryName(sourceFile)!, "Snapshots", "openapi-v1.json");
}

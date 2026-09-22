extern alias ApiService;

using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using ApiService::CreatorPantry.ApiService.Http;
using CreatorPantry.Domain.Models.Results;
using CreatorPantry.Domain.Models.ServiceModels.Auth;
using CreatorPantry.Tests.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Api;

public sealed class ApiVersioningTests : IDisposable
{
    private readonly WebApplicationFactory<ApiService::Program> _factory =
        new WebApplicationFactory<ApiService::Program>().WithWebHostBuilder(web =>
        {
            TestDatabase.ConfigureWithoutHealthCheck(web);
            web.ConfigureTestServices(services => services.AddScoped<Domain.Facade.Auth.IAuthFacade>(_ =>
                new FakeAuthFacade(OperationResult<PasswordServiceModel>.Success(PasswordServiceModel.ResetRequested))));
        });

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task V1_route_is_served_and_reports_supported_versions()
    {
        var response = await PostResetAsync("/api/v1/auth/password-reset");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("1.0", string.Join(",", response.Headers.GetValues("api-supported-versions")));
    }

    [Theory]
    [InlineData("/api/auth/password-reset")] // unversioned: no duplicate route and no default version
    [InlineData("/api/v2/auth/password-reset")] // unsupported
    [InlineData("/api/v1.1/auth/password-reset")] // unsupported minor version
    [InlineData("/api/vX/auth/password-reset")] // malformed
    public async Task Unversioned_unsupported_and_malformed_versions_are_404_problem_details(string path)
    {
        var response = await PostResetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(ProblemResults.NotFoundCode, body.GetProperty("code").GetString());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("traceId").GetString()));
    }

    [Fact]
    public async Task Wrong_method_is_problem_details_with_a_stable_code()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/v1/auth/password-reset", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Equal("method_not_allowed", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Application_error_codes_are_not_overwritten()
    {
        var response = await PostResetAsync("/api/v1/auth/password-reset", "{ not json");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ProblemResults.MalformedRequestCode, body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Health_endpoints_remain_unversioned()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/health", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public void Api_explorer_groups_routes_by_version_with_the_version_substituted()
    {
        var provider = _factory.Services.GetRequiredService<IApiDescriptionGroupCollectionProvider>();

        var group = Assert.Single(provider.ApiDescriptionGroups.Items, g => g.GroupName == "v1");
        Assert.Contains(group.Items, description => description.RelativePath == "api/v1/auth/register");
        Assert.DoesNotContain(group.Items, description => description.RelativePath!.Contains("{version"));
    }

    [Fact]
    public void Every_controller_route_is_versioned_through_the_route_constraint()
    {
        var controllers = typeof(ApiVersioning).Assembly.GetTypes()
            .Where(type => typeof(ControllerBase).IsAssignableFrom(type) && !type.IsAbstract)
            .ToList();

        Assert.NotEmpty(controllers);
        Assert.All(controllers, controller =>
        {
            var template = controller.GetCustomAttribute<RouteAttribute>()?.Template;
            Assert.StartsWith("api/v{version:apiVersion}/", template);
            Assert.NotNull(controller.GetCustomAttribute<Asp.Versioning.ApiVersionAttribute>());
        });
    }

    private async Task<HttpResponseMessage> PostResetAsync(string path, string? rawJson = null)
    {
        using var client = _factory.CreateClient();
        return rawJson is null
            ? await client.PostAsJsonAsync(path, AuthBusinessTests.ResetRequest(), TestContext.Current.CancellationToken)
            : await client.PostAsync(path, new StringContent(rawJson, System.Text.Encoding.UTF8, "application/json"),
                TestContext.Current.CancellationToken);
    }
}

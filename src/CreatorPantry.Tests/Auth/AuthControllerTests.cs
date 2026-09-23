extern alias ApiService;

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ApiService::CreatorPantry.ApiService.Controllers;
using CreatorPantry.Domain.Modules.Auth;
using CreatorPantry.Domain.Modules.Auth.Managers;
using CreatorPantry.Domain.Modules.Auth.Facade;
using CreatorPantry.Domain.Managers.Results;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Auth;

public class AuthControllerTests
{
    private const string RegisterRoute = "/api/v1/auth/register";
    private const string ResetRoute = "/api/v1/auth/password-reset";
    private const string CompleteRoute = "/api/v1/auth/password-reset/complete";

    [Fact]
    public void Controller_depends_only_on_the_auth_facade()
    {
        var parameters = typeof(AuthController).GetConstructors().Single().GetParameters();

        Assert.Equal([typeof(IAuthFacade)], parameters.Select(p => p.ParameterType));
    }

    [Fact]
    public async Task Accepted_registration_returns_202_with_the_pending_status()
    {
        using var client = CreateClient(new FakeAuthFacade(OperationResult<RegistrationServiceModel>.Success(RegistrationServiceModel.PendingConfirmation)));

        var response = await client.PostAsJsonAsync(RegisterRoute, RegisterUserViewModelValidatorTests.Valid(), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Equal("pending_confirmation", body.GetProperty("status").GetString());
        Assert.Single(body.EnumerateObject());
    }

    [Fact]
    public async Task Reset_request_returns_202_with_the_requested_status()
    {
        using var client = CreateClient(new FakeAuthFacade(OperationResult<PasswordServiceModel>.Success(PasswordServiceModel.ResetRequested)));

        var response = await client.PostAsJsonAsync(ResetRoute, AuthBusinessTests.ResetRequest(), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("reset_requested", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Reset_completion_returns_200_with_the_reset_status()
    {
        using var client = CreateClient(new FakeAuthFacade(OperationResult<PasswordServiceModel>.Success(PasswordServiceModel.ResetCompleted)));

        var response = await client.PostAsJsonAsync(CompleteRoute, AuthBusinessTests.CompleteRequest(), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("password_reset", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Invalid_registration_returns_problem_details_with_code_fields_and_trace_id()
    {
        var error = OperationError.Validation(AuthErrorCodes.RegistrationInvalid, "The request is invalid.",
            [("Email", "Enter a valid email address.")]);
        using var client = CreateClient(new FakeAuthFacade(OperationResult<RegistrationServiceModel>.Failure(error)));

        var response = await client.PostAsJsonAsync(RegisterRoute, RegisterUserViewModelValidatorTests.Valid(), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(AuthErrorCodes.RegistrationInvalid, body.GetProperty("code").GetString());
        Assert.Equal("Enter a valid email address.", body.GetProperty("errors").GetProperty("email")[0].GetString());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("traceId").GetString()));
    }

    [Fact]
    public async Task Invalid_token_returns_problem_details_with_the_stable_code()
    {
        var error = new OperationError(AuthErrorCodes.PasswordResetInvalidToken, "This reset link is invalid or has expired.",
            new Dictionary<string, string[]>());
        using var client = CreateClient(new FakeAuthFacade(OperationResult<PasswordServiceModel>.Failure(error)));

        var response = await client.PostAsJsonAsync(CompleteRoute, AuthBusinessTests.CompleteRequest(), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(AuthErrorCodes.PasswordResetInvalidToken, body.GetProperty("code").GetString());
        Assert.Equal("This reset link is invalid or has expired.", body.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Unreadable_body_returns_the_malformed_request_code()
    {
        var facade = new FakeAuthFacade(OperationResult<RegistrationServiceModel>.Success(RegistrationServiceModel.PendingConfirmation));
        using var client = CreateClient(facade);

        var response = await client.PostAsync(RegisterRoute, new StringContent("{ not json", Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("request.malformed", body.GetProperty("code").GetString());
        Assert.Equal(0, facade.Calls);
    }

    [Fact]
    public async Task Unexpected_failure_returns_500_problem_details_without_exception_detail()
    {
        using var client = CreateClient(new FakeAuthFacade(error: new InvalidOperationException("secret detail")));

        var response = await client.PostAsJsonAsync(RegisterRoute, RegisterUserViewModelValidatorTests.Valid(), TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var body = JsonDocument.Parse(text).RootElement;

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("internal_error", body.GetProperty("code").GetString());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("traceId").GetString()));
        Assert.DoesNotContain("secret detail", text);
    }

    private static HttpClient CreateClient(IAuthFacade facade) =>
        new WebApplicationFactory<ApiService::Program>()
            .WithWebHostBuilder(web =>
            {
                TestDatabase.ConfigureWithoutHealthCheck(web);
                web.ConfigureTestServices(services => services.AddScoped(_ => facade));
            })
            .CreateClient();
}

extern alias ApiService;
extern alias Gateway;

using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using ApiService::CreatorPantry.ApiService.Authorization;
using CreatorPantry.Domain.Auth;
using CreatorPantry.ServiceDefaults;
using CreatorPantry.Tests.Gateway;
using Gateway::CreatorPantry.Gateway.InternalTokens;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace CreatorPantry.Tests.Auth;

/// <summary>Gateway issuance of the internal token (baseline B-13).</summary>
public sealed class InternalTokenIssuerTests : IAsyncLifetime
{
    private static readonly ClaimsPrincipal SessionUser = new(new ClaimsIdentity(
    [
        new Claim(ClaimTypes.NameIdentifier, "user-1"),
        new Claim(InternalTokenDefaults.SessionIdClaim, "session-1"),
        new Claim(ClaimTypes.Role, PlatformRoles.PlatformAdmin),
    ], authenticationType: "bff-session"));

    private WebApplication _echo = null!;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        _echo = builder.Build();
        _echo.Map("/{**path}", (HttpRequest request) => Results.Json(new GatewayProxyTests.EchoedRequest(
            request.Path.Value!,
            request.Headers.ToDictionary(header => header.Key, header => header.Value.ToString(), StringComparer.OrdinalIgnoreCase))));
        await _echo.StartAsync();
    }

    public async ValueTask DisposeAsync() => await _echo.DisposeAsync();

    [Fact]
    public async Task Authenticated_session_forwards_a_signed_short_lived_token_instead_of_client_credentials()
    {
        var headers = await SendAsync(SessionUser, clientAuthorization: "Bearer client-forged");

        var authorization = headers["Authorization"];
        Assert.StartsWith("Bearer ", authorization);
        var token = new JsonWebToken(authorization["Bearer ".Length..]);

        Assert.Equal(InternalTokenDefaults.Algorithm, token.Alg);
        Assert.Equal(InternalTokenDefaults.Issuer, token.Issuer);
        Assert.Equal([InternalTokenDefaults.Audience], token.Audiences);
        Assert.Equal("user-1", token.Subject);
        Assert.Equal("session-1", token.GetClaim(InternalTokenDefaults.SessionIdClaim).Value);
        Assert.Equal(PlatformRoles.PlatformAdmin, token.GetClaim(InternalTokenDefaults.RoleClaim).Value);
        Assert.Equal(InternalTokenDefaults.DefaultLifetime, token.ValidTo - token.ValidFrom);
        Assert.DoesNotContain("client-forged", authorization);
    }

    [Fact]
    public async Task Anonymous_request_forwards_no_authorization_even_if_the_client_sent_one()
    {
        var headers = await SendAsync(sessionUser: null, clientAuthorization: "Bearer client-forged");

        Assert.False(headers.ContainsKey("Authorization"));
    }

    [Fact]
    public async Task Forwarded_token_is_accepted_by_the_api_validator()
    {
        var headers = await SendAsync(SessionUser, clientAuthorization: null);
        using var api = new WebApplicationFactory<ApiService::Program>().WithWebHostBuilder(TestDatabase.ConfigureWithoutHealthCheck);

        var result = await InternalTokenValidationTests.AuthenticateAsync(api, headers["Authorization"]["Bearer ".Length..]);

        Assert.True(result.Succeeded);
        Assert.Equal("user-1", result.Principal!.Identity!.Name);
        Assert.True(result.Principal.IsInRole(PlatformRoles.PlatformAdmin));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a pem")]
    public void Gateway_refuses_to_start_without_a_valid_signing_key(string? pem)
    {
        var failures = Validate(new InternalTokenOptions { SigningKeyPem = pem ?? string.Empty });

        Assert.Contains(failures, failure => failure.Contains("SigningKeyPem", StringComparison.Ordinal));
    }

    [Fact]
    public void Gateway_rejects_a_public_only_or_non_p256_key()
    {
        Assert.NotEmpty(Validate(new InternalTokenOptions { SigningKeyPem = TestKeyPair.Shared.PublicKeyPem }));
        Assert.NotEmpty(Validate(new InternalTokenOptions { SigningKeyPem = TestKeyPair.Create(ECCurve.NamedCurves.nistP384).PrivateKeyPem }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(121)]
    public void Gateway_rejects_lifetimes_outside_the_two_minute_cap(int seconds)
    {
        var failures = Validate(new InternalTokenOptions
        {
            SigningKeyPem = TestKeyPair.Shared.PrivateKeyPem,
            Lifetime = TimeSpan.FromSeconds(seconds),
        });

        Assert.Contains(failures, failure => failure.Contains("Lifetime", StringComparison.Ordinal));
    }

    [Fact]
    public void Validation_failures_never_echo_key_material()
    {
        var secretLooking = "-----BEGIN PRIVATE KEY-----\nc2VjcmV0LWtleS1tYXRlcmlhbA==\n-----END PRIVATE KEY-----";

        var failures = Validate(new InternalTokenOptions { SigningKeyPem = secretLooking });

        Assert.All(failures, failure => Assert.DoesNotContain("c2VjcmV0", failure));
    }

    private static IEnumerable<string> Validate(InternalTokenOptions options)
    {
        var services = new ServiceCollection().AddInternalTokens(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();
        var result = provider.GetRequiredService<IValidateOptions<InternalTokenOptions>>().Validate(Options.DefaultName, options);
        return result.Failed ? result.Failures : [];
    }

    private async Task<Dictionary<string, string>> SendAsync(ClaimsPrincipal? sessionUser, string? clientAuthorization)
    {
        using var gateway = GatewayProxyTests.CreateGateway(() => _echo.GetTestServer().CreateHandler(), sessionUser);
        using var client = gateway.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/anything");
        if (clientAuthorization is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", clientAuthorization);
        }

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var echoed = await response.Content.ReadFromJsonAsync<GatewayProxyTests.EchoedRequest>(TestContext.Current.CancellationToken);
        return echoed!.Headers;
    }
}

/// <summary>API validation of the internal token: only the gateway's short-lived ES256 token is accepted.</summary>
public sealed class InternalTokenValidationTests : IDisposable
{
    private readonly WebApplicationFactory<ApiService::Program> _api =
        new WebApplicationFactory<ApiService::Program>().WithWebHostBuilder(TestDatabase.ConfigureWithoutHealthCheck);

    public void Dispose() => _api.Dispose();

    [Fact]
    public async Task Gateway_token_is_accepted()
    {
        var result = await AuthenticateAsync(_api, CreateToken());

        Assert.True(result.Succeeded);
        Assert.Equal("user-1", result.Principal!.Identity!.Name);
    }

    public static TheoryData<string> RejectedTokens => new()
    {
        "wrong-issuer", "wrong-audience", "wrong-key", "expired", "not-yet-valid", "too-long-lived", "hmac", "unsigned",
    };

    [Theory]
    [MemberData(nameof(RejectedTokens))]
    public async Task Tokens_not_minted_by_the_gateway_are_rejected(string variant)
    {
        var now = DateTime.UtcNow;
        var token = variant switch
        {
            "wrong-issuer" => CreateToken(issuer: "someone-else"),
            "wrong-audience" => CreateToken(audience: "another-api"),
            "wrong-key" => CreateToken(key: TestKeyPair.Create()),
            "expired" => CreateToken(notBefore: now.AddMinutes(-5), expires: now.AddMinutes(-4)),
            "not-yet-valid" => CreateToken(notBefore: now.AddMinutes(4), expires: now.AddMinutes(5)),
            "too-long-lived" => CreateToken(notBefore: now.AddSeconds(-10), expires: now.AddMinutes(10)),
            "hmac" => CreateHmacToken(),
            "unsigned" => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
            {
                Issuer = InternalTokenDefaults.Issuer,
                Audience = InternalTokenDefaults.Audience,
                Subject = new ClaimsIdentity([new Claim("sub", "user-1")]),
                Expires = now.AddMinutes(1),
            }),
            _ => throw new ArgumentOutOfRangeException(nameof(variant)),
        };

        var result = await AuthenticateAsync(_api, token);

        Assert.False(result.Succeeded, variant);
    }

    [Fact]
    public async Task Missing_token_is_not_an_authenticated_request()
    {
        using var scope = _api.Services.CreateScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };

        var result = await context.AuthenticateAsync(JwtBearerDefaults.AuthenticationScheme);

        Assert.True(result.None);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not a pem")]
    [InlineData("private")]
    public void Api_refuses_to_start_without_the_gateway_public_key(string? variant)
    {
        var value = variant == "private" ? TestKeyPair.Shared.PrivateKeyPem : variant;
        using var api = new WebApplicationFactory<ApiService::Program>().WithWebHostBuilder(web =>
        {
            TestDatabase.ConfigureWithoutHealthCheck(web);
            web.UseSetting("InternalToken:PublicKeyPem", value ?? string.Empty);
        });

        var error = Assert.Throws<OptionsValidationException>(() => api.CreateClient());

        Assert.Contains("PublicKeyPem", error.Message);
    }

    [Fact]
    public void Every_controller_action_requires_authentication_unless_explicitly_anonymous()
    {
        var actions = _api.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .Where(endpoint => endpoint.Metadata.GetMetadata<ControllerActionDescriptor>() is not null)
            .ToList();

        Assert.NotEmpty(actions);
        Assert.All(actions, endpoint => Assert.True(
            endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null
                || endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Count > 0,
            $"{endpoint.DisplayName} has no authorization requirement."));
    }

    internal static async Task<AuthenticateResult> AuthenticateAsync(WebApplicationFactory<ApiService::Program> api, string token)
    {
        using var scope = api.Services.CreateScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Request.Headers.Authorization = $"Bearer {token}";

        return await context.AuthenticateAsync(JwtBearerDefaults.AuthenticationScheme);
    }

    private static string CreateToken(
        string issuer = InternalTokenDefaults.Issuer,
        string audience = InternalTokenDefaults.Audience,
        TestKeyPair? key = null,
        DateTime? notBefore = null,
        DateTime? expires = null)
    {
        // Not disposed: IdentityModel caches signature providers per key, and a disposed key poisons that cache.
        var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem((key ?? TestKeyPair.Shared).PrivateKeyPem);
        var now = DateTime.UtcNow;

        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Subject = new ClaimsIdentity([new Claim("sub", "user-1"), new Claim(InternalTokenDefaults.SessionIdClaim, "s")]),
            NotBefore = notBefore ?? now,
            IssuedAt = notBefore ?? now,
            Expires = expires ?? now.AddMinutes(1),
            SigningCredentials = new SigningCredentials(new ECDsaSecurityKey(ecdsa), SecurityAlgorithms.EcdsaSha256),
        });
    }

    private static string CreateHmacToken() => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
    {
        Issuer = InternalTokenDefaults.Issuer,
        Audience = InternalTokenDefaults.Audience,
        Subject = new ClaimsIdentity([new Claim("sub", "user-1")]),
        Expires = DateTime.UtcNow.AddMinutes(1),
        SigningCredentials = new SigningCredentials(
            new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32)), SecurityAlgorithms.HmacSha256),
    });
}

extern alias ApiService;

using System.Security.Claims;
using ApiService::CreatorPantry.ApiService.Authorization;
using CreatorPantry.Domain.Auth;
using CreatorPantry.ServiceDefaults;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Auth;

public sealed class AuthorizationPolicyTests : IDisposable
{
    private readonly WebApplicationFactory<ApiService::Program> _factory =
        new WebApplicationFactory<ApiService::Program>().WithWebHostBuilder(TestDatabase.ConfigureWithoutHealthCheck);

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Authenticated_platform_admin_satisfies_the_policy()
    {
        Assert.True(await AuthorizeAsync(User(authenticated: true, PlatformRoles.PlatformAdmin)));
    }

    [Fact]
    public async Task Authenticated_user_without_the_role_is_denied()
    {
        Assert.False(await AuthorizeAsync(User(authenticated: true)));
    }

    [Theory]
    [InlineData("Owner")] // workspace roles never stand in for platform roles
    [InlineData("platformadmin")] // role checks are exact
    public async Task Other_role_claims_are_denied(string role)
    {
        Assert.False(await AuthorizeAsync(User(authenticated: true, role)));
    }

    [Fact]
    public async Task Unauthenticated_principal_with_the_role_claim_is_denied()
    {
        Assert.False(await AuthorizeAsync(User(authenticated: false, PlatformRoles.PlatformAdmin)));
    }

    private async Task<bool> AuthorizeAsync(ClaimsPrincipal user)
    {
        using var scope = _factory.Services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<IAuthorizationService>()
            .AuthorizeAsync(user, resource: null, AuthorizationPolicies.PlatformAdmin);
        return result.Succeeded;
    }

    [Fact]
    public async Task Gateway_service_token_with_the_role_is_denied()
    {
        // Service tokens authenticate the gateway itself, never a user.
        var service = UserWithTokenUse(InternalTokenDefaults.ServiceTokenUse, [PlatformRoles.PlatformAdmin]);

        Assert.False(await AuthorizeAsync(service));
    }

    private static ClaimsPrincipal User(bool authenticated, params string[] roles) =>
        UserWithTokenUse(InternalTokenDefaults.UserTokenUse, roles, authenticated);

    private static ClaimsPrincipal UserWithTokenUse(string tokenUse, string[] roles, bool authenticated = true)
    {
        var claims = roles.Select(role => new Claim(ClaimTypes.Role, role))
            .Append(new Claim(ClaimTypes.NameIdentifier, "u1"))
            .Append(new Claim(InternalTokenDefaults.TokenUseClaim, tokenUse));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: authenticated ? "test" : null));
    }
}

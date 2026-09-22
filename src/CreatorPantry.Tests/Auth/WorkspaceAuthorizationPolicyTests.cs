extern alias ApiService;

using System.Security.Claims;
using ApiService::CreatorPantry.ApiService.Authorization;
using CreatorPantry.Domain.Auth;
using CreatorPantry.Domain.Tenancy;
using CreatorPantry.ServiceDefaults;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Auth;

/// <summary>
/// Every <c>Workspace*</c> policy against every <see cref="WorkspaceRole"/>, proving role implication (a
/// higher role satisfies a lower policy) works from the single shared
/// <see cref="ApiService::CreatorPantry.ApiService.Authorization.WorkspaceRoleRequirement"/> without any
/// per-policy special-casing, plus the fail-closed cases: an unresolved workspace context, and a gateway
/// service token (never a user, however a resolved role might otherwise look).
/// </summary>
public sealed class WorkspaceAuthorizationPolicyTests : IDisposable
{
    private static readonly string[] Policies =
    [
        AuthorizationPolicies.WorkspaceViewer,
        AuthorizationPolicies.WorkspaceContributor,
        AuthorizationPolicies.WorkspaceEditor,
        AuthorizationPolicies.WorkspaceOwner,
    ];

    private static readonly Dictionary<string, WorkspaceRole> MinimumRoleFor = new()
    {
        [AuthorizationPolicies.WorkspaceViewer] = WorkspaceRole.Viewer,
        [AuthorizationPolicies.WorkspaceContributor] = WorkspaceRole.Contributor,
        [AuthorizationPolicies.WorkspaceEditor] = WorkspaceRole.Editor,
        [AuthorizationPolicies.WorkspaceOwner] = WorkspaceRole.Owner,
    };

    private readonly WebApplicationFactory<ApiService::Program> _factory =
        new WebApplicationFactory<ApiService::Program>().WithWebHostBuilder(TestDatabase.ConfigureWithoutHealthCheck);

    public void Dispose() => _factory.Dispose();

    public static IEnumerable<object[]> EveryRoleAgainstEveryPolicy() =>
        from role in Enum.GetValues<WorkspaceRole>()
        from policy in Policies
        select new object[] { role, policy };

    [Theory]
    [MemberData(nameof(EveryRoleAgainstEveryPolicy))]
    public async Task Role_satisfies_policy_iff_at_least_the_policys_minimum(WorkspaceRole role, string policy)
    {
        var expected = role >= MinimumRoleFor[policy];

        Assert.Equal(expected, await AuthorizeAsResolvedAsync(role, policy));
    }

    [Theory]
    [MemberData(nameof(AllPolicies))]
    public async Task An_unresolved_workspace_context_is_denied_by_every_policy(string policy)
    {
        using var scope = _factory.Services.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<IAuthorizationService>()
            .AuthorizeAsync(User(), resource: null, policy);

        Assert.False(result.Succeeded);
    }

    [Theory]
    [MemberData(nameof(AllPolicies))]
    public async Task A_gateway_service_token_is_denied_even_with_a_resolved_owner_role(string policy)
    {
        using var scope = _factory.Services.CreateScope();
        Resolve(scope, WorkspaceRole.Owner);
        var service = UserWithTokenUse(InternalTokenDefaults.ServiceTokenUse);

        var result = await scope.ServiceProvider.GetRequiredService<IAuthorizationService>()
            .AuthorizeAsync(service, resource: null, policy);

        Assert.False(result.Succeeded);
    }

    public static IEnumerable<object[]> AllPolicies() => Policies.Select(policy => new object[] { policy });

    private async Task<bool> AuthorizeAsResolvedAsync(WorkspaceRole role, string policy)
    {
        using var scope = _factory.Services.CreateScope();
        Resolve(scope, role);

        var result = await scope.ServiceProvider.GetRequiredService<IAuthorizationService>()
            .AuthorizeAsync(User(), resource: null, policy);
        return result.Succeeded;
    }

    private static void Resolve(IServiceScope scope, WorkspaceRole role) =>
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>()
            .Resolve(Guid.NewGuid(), "sams-kitchen", Guid.NewGuid(), role);

    private static ClaimsPrincipal User() => UserWithTokenUse(InternalTokenDefaults.UserTokenUse);

    private static ClaimsPrincipal UserWithTokenUse(string tokenUse)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "u1"),
            new Claim(InternalTokenDefaults.TokenUseClaim, tokenUse),
        };
        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "test"));
    }
}

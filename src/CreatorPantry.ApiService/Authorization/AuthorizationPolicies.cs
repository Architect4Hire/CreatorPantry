using CreatorPantry.Domain.Modules.Auth;
using CreatorPantry.Domain.Modules.Auth.Managers;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.ServiceDefaults;
using Microsoft.AspNetCore.Authorization;

namespace CreatorPantry.ApiService.Authorization;

/// <summary>Named authorization policies. Workspace access is decided by membership policies, not these.</summary>
public static class AuthorizationPolicies
{
    /// <summary>
    /// An authenticated user holding the platform-wide <see cref="PlatformRoles.PlatformAdmin"/> role. It does
    /// not grant access to any workspace's content; support access needs a separate audited policy.
    /// </summary>
    public const string PlatformAdmin = "PlatformAdmin";

    /// <summary>The gateway's own service identity; used only by the internal session routes.</summary>
    public const string GatewayService = "GatewayService";

    /// <summary>
    /// A machine client holding an ops API key with the AI-usage administration scope (baseline B-14).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Built from its own builder, not <see cref="UserPolicy"/>.</strong> Every other policy here
    /// demands <c>token_use=user</c>, a claim only the gateway mints — so an ops key could never satisfy one,
    /// and <see cref="PlatformAdmin"/> in particular cannot be reused for machine access however much the two
    /// sound alike.
    /// </para>
    /// <para>
    /// <strong>It names its authentication scheme.</strong> That is what keeps the two credentials apart in
    /// both directions: the JWT scheme never runs on an ops route, so a gateway-minted user token — even one
    /// carrying the <c>PlatformAdmin</c> role — is refused there; and an ops key is never evaluated anywhere
    /// else, so it cannot reach a product route.
    /// </para>
    /// </remarks>
    public const string Ops = "Ops";

    /// <summary>The caller's role in the resolved workspace is at least <see cref="WorkspaceRole.Viewer"/>.</summary>
    public const string WorkspaceViewer = "WorkspaceViewer";

    /// <summary>The caller's role in the resolved workspace is at least <see cref="WorkspaceRole.Contributor"/>.</summary>
    public const string WorkspaceContributor = "WorkspaceContributor";

    /// <summary>The caller's role in the resolved workspace is at least <see cref="WorkspaceRole.Editor"/>.</summary>
    public const string WorkspaceEditor = "WorkspaceEditor";

    /// <summary>The caller's role in the resolved workspace is at least <see cref="WorkspaceRole.Owner"/>.</summary>
    public const string WorkspaceOwner = "WorkspaceOwner";

    public static IServiceCollection AddCreatorPantryAuthorization(this IServiceCollection services) =>
        services.AddAuthorizationBuilder()
            // [Authorize] and MapAuthenticatedControllers mean "a signed-in user": gateway service tokens
            // cannot call product routes.
            .SetDefaultPolicy(UserPolicy().Build())
            .AddPolicy(PlatformAdmin, UserPolicy().RequireRole(PlatformRoles.PlatformAdmin).Build())
            .AddPolicy(GatewayService, policy => policy
                .RequireAuthenticatedUser()
                .RequireClaim(InternalTokenDefaults.TokenUseClaim, InternalTokenDefaults.ServiceTokenUse)
                .RequireClaim("sub", InternalTokenDefaults.GatewayServiceSubject))
            .AddPolicy(Ops, policy => policy
                .AddAuthenticationSchemes(OpsApiKeyPolicy.Scheme)
                .RequireAuthenticatedUser()
                .RequireClaim(OpsClaims.Scope, OpsScopes.AiUsageAdmin))
            .AddPolicy(WorkspaceViewer, WorkspaceRolePolicy(WorkspaceRole.Viewer))
            .AddPolicy(WorkspaceContributor, WorkspaceRolePolicy(WorkspaceRole.Contributor))
            .AddPolicy(WorkspaceEditor, WorkspaceRolePolicy(WorkspaceRole.Editor))
            .AddPolicy(WorkspaceOwner, WorkspaceRolePolicy(WorkspaceRole.Owner))
            .Services
            // Scoped, not Singleton: reads the request-scoped IWorkspaceContext.
            .AddScoped<IAuthorizationHandler, WorkspaceRoleAuthorizationHandler>();

    /// <summary>
    /// Maps controllers so every action without its own authorization metadata requires a signed-in user;
    /// actions opt out with <c>[AllowAnonymous]</c> or choose a policy with <c>[Authorize(Policy = ...)]</c>
    /// (which would otherwise be combined with, and blocked by, the default user policy). Applied to controller
    /// endpoints rather than as a global fallback policy so the framework's 404 (unsupported API version) and
    /// 405 endpoints keep their status codes.
    /// </summary>
    public static ControllerActionEndpointConventionBuilder MapAuthenticatedControllers(this IEndpointRouteBuilder endpoints)
    {
        var controllers = endpoints.MapControllers();
        controllers.Add(endpoint =>
        {
            if (!endpoint.Metadata.OfType<IAuthorizeData>().Any() && !endpoint.Metadata.OfType<IAllowAnonymous>().Any())
            {
                endpoint.Metadata.Add(new AuthorizeAttribute());
            }
        });

        return controllers;
    }

    private static AuthorizationPolicyBuilder UserPolicy() => new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .RequireClaim(InternalTokenDefaults.TokenUseClaim, InternalTokenDefaults.UserTokenUse);

    /// <summary>
    /// A signed-in product user whose role in the resolved workspace is at least <paramref name="minimumRole"/>,
    /// via the single shared <see cref="WorkspaceRoleRequirement"/>/<see cref="WorkspaceRoleAuthorizationHandler"/>
    /// pair. The four <c>Workspace*</c> policies differ only in this argument.
    /// </summary>
    private static AuthorizationPolicy WorkspaceRolePolicy(WorkspaceRole minimumRole) =>
        UserPolicy().AddRequirements(new WorkspaceRoleRequirement(minimumRole)).Build();
}

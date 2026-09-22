using System.Text.RegularExpressions;
using CreatorPantry.ApiService.Http;
using CreatorPantry.Domain.Facade.Tenancy;
using CreatorPantry.Domain.Models.ViewModels.Tenancy;
using CreatorPantry.Domain.Tenancy;
using CreatorPantry.ServiceDefaults;
using Microsoft.IdentityModel.JsonWebTokens;

namespace CreatorPantry.ApiService.Tenancy;

/// <summary>
/// Resolves <c>{workspaceSlug}</c> from <c>/api/v{version}/workspaces/{workspaceSlug}/...</c> and populates
/// <see cref="IWorkspaceContext"/> before authorization and controllers run. No workspace-scoped controller
/// exists yet, so the shape is recognized from the raw request path rather than an MVC route template.
/// </summary>
public static partial class WorkspaceResolutionMiddleware
{
    /// <summary>Insert between <c>UseAuthentication</c> and <c>UseAuthorization</c>.</summary>
    public static IApplicationBuilder UseWorkspaceResolution(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var match = WorkspaceRoute().Match(context.Request.Path.Value ?? string.Empty);
            if (!match.Success)
            {
                await next(context);
                return;
            }

            // Only an authenticated user token carries a caller identity worth resolving. An unauthenticated
            // request or the gateway's own service token is left for UseAuthorization to reject downstream;
            // this middleware never treats a service token's fixed subject as a user id.
            if (!context.User.HasClaim(InternalTokenDefaults.TokenUseClaim, InternalTokenDefaults.UserTokenUse))
            {
                await next(context);
                return;
            }

            var userId = context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
            if (string.IsNullOrEmpty(userId))
            {
                await next(context);
                return;
            }

            var facade = context.RequestServices.GetRequiredService<IWorkspaceResolutionFacade>();
            var result = await facade.ResolveAsync(
                userId, new ResolveWorkspaceViewModel(match.Groups["slug"].Value), context.RequestAborted);

            if (!result.Succeeded)
            {
                // Unknown workspace, no membership, and inactive membership are already one indistinguishable
                // error by the time it reaches here (WorkspaceBusiness). No body is written here: an empty
                // response with this status is turned into ProblemDetails by UseStatusCodePages, so this
                // middleware cannot leak anything beyond the status code itself.
                context.Response.StatusCode = ProblemResults.StatusFor(result.Error!.Code);
                return;
            }

            var resolved = result.Value!;
            context.RequestServices.GetRequiredService<IWorkspaceContextResolver>()
                .Resolve(resolved.WorkspaceId, resolved.WorkspaceSlug, resolved.MembershipId, resolved.Role);

            await next(context);
        });

    [GeneratedRegex(@"^/api/v\d+/workspaces/(?<slug>[^/]+)(/.*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WorkspaceRoute();
}

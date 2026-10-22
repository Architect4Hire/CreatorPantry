using System.IdentityModel.Tokens.Jwt;
using CreatorPantry.ApiService.Http;
using CreatorPantry.Domain.Managers.Caching;
using CreatorPantry.Domain.Managers.Limits;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Time;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;

namespace CreatorPantry.ApiService.RateLimiting;

/// <summary>
/// Holds an action to the rates configured for <see cref="Operation"/> (15.3), per user and per workspace. The
/// OpenAPI document reads it to publish the 429 the action can answer (<c>OpenApiDocumentation</c>).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Who is counted comes from the resolved request, never from it.</strong> The user is the account the
/// workspace context resolved, or the token's subject on a route with no workspace; the workspace is the one the
/// route resolved through membership. Nothing in a body, query or header chooses either (tenancy.md).
/// </para>
/// <para>
/// <strong>After authorization, and before the body is read where it can be.</strong> A request refused for
/// permission costs nothing. Most operations cost one whatever they carry, so they are checked as a resource
/// filter — before model binding, so a refused upload is never buffered. An AI request is checked after binding
/// instead, because its model can say what it will cost (<see cref="ILimitCost"/>): a creative-context decision
/// that regenerates nothing costs none.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class OperationLimitAttribute(LimitedOperation operation) : Attribute, IFilterFactory
{
    public LimitedOperation Operation { get; } = operation;

    public bool IsReusable => false;

    public IFilterMetadata CreateInstance(IServiceProvider serviceProvider) =>
        ActivatorUtilities.CreateInstance<OperationLimitFilter>(serviceProvider, Operation);
}

/// <summary>The check itself: the user's bucket, then the workspace's, each only where a rule is configured.</summary>
internal sealed class OperationLimitFilter(
    LimitedOperation operation,
    IRateLimitStore store,
    IWorkspaceContext workspace,
    IClock clock,
    IOptions<LimitsOptions> options) : IAsyncResourceFilter, IAsyncActionFilter
{
    /// <summary>Whether the bound model decides the cost, which only an AI request's does.</summary>
    private bool CostsByModel => operation is LimitedOperation.AiRequest;

    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        if (!CostsByModel && await RefusalAsync(context.HttpContext, cost: 1) is { } refusal)
        {
            context.Result = refusal;
            return;
        }

        await next();
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (CostsByModel && await RefusalAsync(context.HttpContext, Cost(context)) is { } refusal)
        {
            context.Result = refusal;
            return;
        }

        await next();
    }

    private async Task<IActionResult?> RefusalAsync(HttpContext http, int cost)
    {
        var limits = options.Value;

        if (!limits.Enabled || cost == 0)
        {
            return null;
        }

        var rule = limits.RateFor(operation);
        var now = clock.UtcNow;
        var cancellationToken = http.RequestAborted;

        if (rule.User is { IsValid: true } userRule
            && UserOf(http) is { } userId
            && await RefusedAsync(http, UserKey(userId), userRule, LimitScope.User, cost, now, cancellationToken) is { } byUser)
        {
            return byUser;
        }

        if (rule.Workspace is { IsValid: true } workspaceRule && workspace.IsResolved)
        {
            var key = CacheKeys.Workspace(workspace.WorkspaceId, "limits", operation.ToString());

            return await RefusedAsync(http, key, workspaceRule, LimitScope.Workspace, cost, now, cancellationToken);
        }

        return null;
    }

    private async Task<IActionResult?> RefusedAsync(
        HttpContext http,
        string key,
        TokenBucketRule rule,
        LimitScope scope,
        int cost,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var decision = await store.TryTakeAsync(key, rule, cost, now, cancellationToken);

        if (decision.FellBack)
        {
            LimitsTelemetry.FellBack(operation, scope);
        }

        if (decision.Allowed)
        {
            LimitsTelemetry.Admitted(operation, "rate", scope);
            return null;
        }

        LimitsTelemetry.Refused(operation, "rate", scope);

        // Whole seconds, rounded up: an instant part-way through a second would be written as the one before it,
        // and a client that came back then would be refused again.
        var resetsAt = now + TimeSpan.FromSeconds(Math.Ceiling(decision.RetryAfter.TotalSeconds));

        return ProblemResults.ProblemFor(http, LimitRefusal.Rate(operation, scope, rule.Burst, resetsAt));
    }

    /// <summary>What the bound request will cost: one, unless its model says otherwise.</summary>
    private static int Cost(ActionExecutingContext context) =>
        context.ActionArguments.Values.OfType<ILimitCost>().FirstOrDefault() is { } costed
            ? Math.Max(0, costed.LimitCost)
            : 1;

    private string? UserOf(HttpContext http) =>
        workspace.IsResolved && !string.IsNullOrEmpty(workspace.AccountId)
            ? workspace.AccountId
            : http.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;

    /// <summary>
    /// The person's own bucket, the same in every workspace they work in. An account key, never a workspace one:
    /// it counts one person's requests and nobody else's.
    /// </summary>
    private string UserKey(string userId) => $"account:{userId}:limits:{operation}";
}

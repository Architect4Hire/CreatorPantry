using CreatorPantry.Domain.Managers.Limits;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Media.Data;
using CreatorPantry.Domain.Modules.Media.Facade;
using CreatorPantry.Domain.Modules.Media.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CreatorPantry.Tests.Media;

/// <summary>
/// The image limits (15.3): requests waiting per workspace, the day's variants per workspace — at the request and
/// again in the worker — and a fair share of the queue.
/// </summary>
public sealed partial class GeneratedImageWorkerTests
{
    private LimitsOptions Limits => _provider.GetRequiredService<IOptions<LimitsOptions>>().Value;

    [Fact]
    public async Task A_workspace_with_its_share_of_requests_waiting_is_refused_and_the_other_is_not()
    {
        Limits.ImageInFlightPerWorkspace = 2;

        await RequestAsync(WorkspaceA);
        await RequestAsync(WorkspaceA);

        var refused = await RequestRawAsync(WorkspaceA);

        Assert.Equal(LimitErrorCodes.ConcurrencyExhausted, refused.Error!.Code);
        Assert.Equal("Workspace", refused.Error.Extensions!["scope"]);
        Assert.Equal(2, (await OperationsAsync(WorkspaceA)).Count);

        // B has nothing waiting, whatever A has.
        await RequestAsync(WorkspaceB);
    }

    [Fact]
    public async Task A_days_variants_are_all_or_none_and_say_when_they_return()
    {
        Limits.ImageVariantsPerWorkspacePerDay = 5;

        await RequestAsync(WorkspaceA, variants: 4);

        var refused = await RequestRawAsync(WorkspaceA, variants: 2);

        Assert.Equal(LimitErrorCodes.BudgetExhausted, refused.Error!.Code);
        Assert.Equal(5, refused.Error.Extensions!["limit"]);
        Assert.Equal(LimitDay.NextAfter(_clock.UtcNow), refused.Error.Extensions["resetsAt"]);

        // What is left of the day is still there to be used.
        await RequestAsync(WorkspaceA, variants: 1);
        Assert.Equal(LimitErrorCodes.BudgetExhausted, (await RequestRawAsync(WorkspaceA)).Error!.Code);
    }

    [Fact]
    public async Task The_day_resets_at_midnight_utc()
    {
        Limits.ImageVariantsPerWorkspacePerDay = 1;

        await RequestAsync(WorkspaceA);
        Assert.Equal(LimitErrorCodes.BudgetExhausted, (await RequestRawAsync(WorkspaceA)).Error!.Code);

        _clock.Advance(LimitDay.NextAfter(_clock.UtcNow) - _clock.UtcNow);

        await RequestAsync(WorkspaceA);
    }

    [Fact]
    public async Task One_workspaces_day_is_not_anothers()
    {
        Limits.ImageVariantsPerWorkspacePerDay = 2;

        await RequestAsync(WorkspaceA, variants: 2);
        Assert.Equal(LimitErrorCodes.BudgetExhausted, (await RequestRawAsync(WorkspaceA)).Error!.Code);

        var theirs = await RequestAsync(WorkspaceB, variants: 2);
        Assert.Equal(2, theirs.VariantCount);
    }

    [Fact]
    public async Task A_replay_is_never_refused_by_a_limit()
    {
        Limits.ImageVariantsPerWorkspacePerDay = 1;
        Limits.ImageInFlightPerWorkspace = 1;

        // One person asking twice: a replay is the same request from the same member.
        await using var scope = ScopeFor(WorkspaceA);
        var facade = scope.ServiceProvider.GetRequiredService<IGeneratedImageGenerationFacade>();

        var first = await facade.RequestAsync(Request(key: "the-one-key"), Ct);

        // Both limits are now spent, and the same key still answers with the operation it bought.
        var again = await facade.RequestAsync(Request(key: "the-one-key"), Ct);

        Assert.True(again.Succeeded, again.Error?.Code);
        Assert.Equal(first.Value!.Id, again.Value!.Id);
        Assert.Single(await OperationsAsync(WorkspaceA));
    }

    [Fact]
    public async Task The_worker_refuses_a_request_past_the_days_variants_before_calling_anyone()
    {
        // Two requests admitted together, as two racing past the request-time check would be — then the day's
        // allowance is the one both were checked against.
        Limits.ImageVariantsPerWorkspacePerDay = 8;
        var first = await RequestAsync(WorkspaceA, variants: 4);
        _clock.Advance(TimeSpan.FromSeconds(1));
        var second = await RequestAsync(WorkspaceA, variants: 4);
        Limits.ImageVariantsPerWorkspacePerDay = 5;

        await RunAsync();

        Assert.Equal(GeneratedImageOperationStatus.Succeeded, (await OperationAsync(WorkspaceA, first.Id)).Status);

        var refused = await OperationAsync(WorkspaceA, second.Id);
        Assert.Equal(GeneratedImageOperationStatus.Failed, refused.Status);
        Assert.Equal(GeneratedImageFailureCategory.BudgetExhausted, refused.FailureCategory);

        // Four calls for the first request's four variants, and none for the second.
        Assert.Equal(4, _generator.Calls);
        Assert.Empty(await ImagesAsync(WorkspaceA, second.Id));
    }

    [Fact]
    public async Task A_workspace_with_its_share_running_waits_while_another_workspace_is_served()
    {
        Limits.ImageInFlightPerWorkspace = 10;

        var running = await RequestAsync(WorkspaceA);
        _clock.Advance(TimeSpan.FromSeconds(1));
        var waiting = await RequestAsync(WorkspaceA);
        _clock.Advance(TimeSpan.FromSeconds(1));
        var theirs = await RequestAsync(WorkspaceB);

        await using var scope = _provider.CreateAsyncScope();
        var claims = scope.ServiceProvider.GetRequiredService<GeneratedImageClaimRepository>();

        // A takes its one place; its next request is older than B's and still passed over for it.
        Assert.Equal(running.Id, (await claims.ClaimNextAsync(Guid.NewGuid(), _clock.UtcNow, Ct, runningPerWorkspace: 1))!.OperationId);
        Assert.Equal(theirs.Id, (await claims.ClaimNextAsync(Guid.NewGuid(), _clock.UtcNow, Ct, runningPerWorkspace: 1))!.OperationId);
        Assert.Null(await claims.ClaimNextAsync(Guid.NewGuid(), _clock.UtcNow, Ct, runningPerWorkspace: 1));

        // Without a share, A's second request is simply next.
        Assert.Equal(waiting.Id, (await claims.ClaimNextAsync(Guid.NewGuid(), _clock.UtcNow, Ct))!.OperationId);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<Domain.Managers.Results.OperationResult<GeneratedImageOperationServiceModel>> RequestRawAsync(
        Guid workspaceId, int variants = 1)
    {
        await using var scope = ScopeFor(workspaceId);

        var result = await scope.ServiceProvider.GetRequiredService<IGeneratedImageGenerationFacade>()
            .RequestAsync(Request(variants), Ct);

        Assert.False(result.Succeeded);

        return result;
    }
}

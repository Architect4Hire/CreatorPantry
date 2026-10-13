using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Publishing.Business;
using CreatorPantry.Domain.Modules.Publishing.Data;
using CreatorPantry.Domain.Modules.Publishing.Data.Entities;
using CreatorPantry.Domain.Modules.Publishing.Facade;
using CreatorPantry.Domain.Modules.Publishing.Managers;
using CreatorPantry.Domain.Modules.Tenancy.Facade;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Tests.Recipes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CreatorPantry.Tests.Publishing;

/// <summary>
/// The worker's own job, as distinct from the matrix it hands work to: claiming, resolving each workspace
/// before anything is read, and spending one listing per channel (13.10c, DATA-RQ-031).
/// </summary>
/// <remarks>
/// The facade is a stand-in here, because what is under test is what the worker does <em>around</em> it. What
/// it decides is covered in <see cref="PublishingReconciliationTests"/>.
/// </remarks>
public sealed class PublishingReconciliationWorkerTests(SqlServerRecipeFixture fixture)
    : IClassFixture<SqlServerRecipeFixture>
{
    private static readonly Guid WsA = SqlServerRecipeFixture.WorkspaceA;
    private static readonly Guid WsB = SqlServerRecipeFixture.WorkspaceB;
    private static readonly DateTimeOffset Now = SqlServerRecipeFixture.Now;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_pass_groups_its_claims_by_workspace_and_then_by_channel()
    {
        var first = await SeedProfileAsync(WsA);
        var second = await SeedProfileAsync(WsA);

        var expected = new Dictionary<Guid, List<Guid>>
        {
            [first] = [await SeedRecordAsync(WsA, first), await SeedRecordAsync(WsA, first)],
            [second] = [await SeedRecordAsync(WsA, second)],
        };

        var facade = new RecordingFacade();
        var summary = await RunAsync(facade, resolvable: [WsA, WsB]);

        Assert.True(summary.Claimed >= 3);

        // One call per channel, not per record: three records on two channels cost two listings.
        foreach (var (profileId, recordIds) in expected)
        {
            var call = Assert.Single(facade.Calls.Where(call => call.ProfileId == profileId));
            Assert.All(recordIds, id => Assert.Contains(id, call.RecordIds));
        }
    }

    [Fact]
    public async Task A_workspace_that_no_longer_resolves_is_skipped_and_nothing_of_its_is_read()
    {
        var profile = await SeedProfileAsync(WsB);
        var recordId = await SeedRecordAsync(WsB, profile);

        // Workspace A resolves; B does not — as it would not if it had been erased.
        var facade = new RecordingFacade();
        var summary = await RunAsync(facade, resolvable: [WsA]);

        // Finding a row was not authorization, and without a resolved workspace the worker reads no further.
        Assert.DoesNotContain(recordId, facade.Calls.SelectMany(call => call.RecordIds));
        Assert.DoesNotContain(profile, facade.Calls.Select(call => call.ProfileId));
        Assert.True(summary.Records.Skipped >= 1);

        var untouched = await InAsync(WsB, db => db.PublishingRecords.AsNoTracking()
            .SingleAsync(record => record.Id == recordId, Ct));
        Assert.Equal(PublishingRecordStatus.Pending, untouched.Status);
    }

    [Fact]
    public async Task One_profile_failing_does_not_stop_the_pass()
    {
        var failing = await SeedProfileAsync(WsA);
        var healthy = await SeedProfileAsync(WsA);
        await SeedRecordAsync(WsA, failing);
        var survivor = await SeedRecordAsync(WsA, healthy);

        var facade = new RecordingFacade { Throwing = failing };
        var summary = await RunAsync(facade, resolvable: [WsA, WsB]);

        // The claims the failed group held lapse and are picked up again; nothing is concluded about them.
        Assert.Contains(survivor, facade.Calls.SelectMany(call => call.RecordIds));
        Assert.True(summary.Claimed >= 2);
    }

    [Fact]
    public async Task A_pass_with_nothing_due_asks_the_provider_nothing()
    {
        var profile = await SeedProfileAsync(WsA);
        await SeedRecordAsync(WsA, profile, availableAt: Now.AddHours(1));

        var facade = new RecordingFacade();

        // Everything already claimable is drained first, so this pass is the quiet one.
        await RunAsync(facade, resolvable: [WsA, WsB]);
        facade.Calls.Clear();

        var summary = await RunAsync(facade, resolvable: [WsA, WsB]);

        Assert.Equal(0, summary.Claimed);
        Assert.Equal(0, summary.Listings);
        Assert.Empty(facade.Calls);
    }

    // ---- Helpers ----

    private async Task<PublishingReconcilePassSummary> RunAsync(RecordingFacade facade, Guid[] resolvable)
    {
        await using var scope = fixture.ScopeFor(WsA);

        // A container holding exactly what the worker resolves per pass, so the test owns both.
        var services = new ServiceCollection()
            .AddScoped<IWorkspaceResolutionFacade>(_ => new ScriptedResolution(resolvable))
            .AddScoped<IPublishingReconciliationFacade>(_ => facade)
            .BuildServiceProvider();

        var worker = new PublishingReconciliationWorker(
            new PublishingReconciliationClaimRepository(SqlServerRecipeFixture.Db(scope)),
            services.GetRequiredService<IServiceScopeFactory>(),
            new FixedClock(),
            NullLogger<PublishingReconciliationWorker>.Instance);

        return await worker.RunPendingAsync(Ct);
    }

    private async Task<Guid> SeedProfileAsync(Guid workspaceId)
    {
        var profile = new PublishingProfile
        {
            Id = Guid.NewGuid(),
            ProviderKey = PublishingPolicy.BufferProviderKey,
            Platform = PublishingPlatform.Instagram,
            DisplayName = "The test kitchen",
            Status = PublishingProfileStatus.Ready,
            FirstSeenAt = Now,
            LastSeenAt = Now,
        };

        await InAsync(workspaceId, async db =>
        {
            db.PublishingProfiles.Add(profile);
            await db.SaveChangesAsync(Ct);
        });

        return profile.Id;
    }

    private async Task<Guid> SeedRecordAsync(Guid workspaceId, Guid profileId, DateTimeOffset? availableAt = null)
    {
        var record = new PublishingRecord
        {
            Id = Guid.NewGuid(),
            PublishingProfileId = profileId,
            Platform = PublishingPlatform.Instagram,
            ContentText = "Soda bread, out of the oven at last.",
            ScheduledAt = Now.AddDays(3),
            ScheduleTimeZoneId = "America/Chicago",
            ScheduledLocal = Now.AddDays(3).UtcDateTime,
            Status = PublishingRecordStatus.Pending,
            StatusChangedAt = Now,
            AvailableAt = availableAt ?? Now,
            ConfirmedByMembershipId = SqlServerRecipeFixture.AuthorOne,
            CreatedAt = Now,
            IdempotencyKey = $"seeded-{Guid.NewGuid():N}",
        };

        await InAsync(workspaceId, async db =>
        {
            db.PublishingRecords.Add(record);
            await db.SaveChangesAsync(Ct);
        });

        return record.Id;
    }

    private async Task InAsync(Guid workspaceId, Func<CreatorPantryDbContext, Task> work)
    {
        await using var scope = fixture.ScopeFor(workspaceId);
        await work(SqlServerRecipeFixture.Db(scope));
    }

    private async Task<T> InAsync<T>(Guid workspaceId, Func<CreatorPantryDbContext, Task<T>> work)
    {
        await using var scope = fixture.ScopeFor(workspaceId);

        return await work(SqlServerRecipeFixture.Db(scope));
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed record FacadeCall(Guid ProfileId, IReadOnlyList<Guid> RecordIds);

    private sealed class RecordingFacade : IPublishingReconciliationFacade
    {
        public List<FacadeCall> Calls { get; } = [];

        /// <summary>A profile whose group throws, standing in for one channel's provider misbehaving.</summary>
        public Guid? Throwing { get; set; }

        public Task<PublishingReconcileSummary> ReconcileProfileAsync(
            Guid publishingProfileId, IReadOnlyList<Guid> recordIds, CancellationToken cancellationToken)
        {
            if (publishingProfileId == Throwing)
            {
                throw new InvalidOperationException("That channel's provider misbehaved.");
            }

            Calls.Add(new FacadeCall(publishingProfileId, recordIds));

            return Task.FromResult(new PublishingReconcileSummary(StillWaiting: recordIds.Count));
        }
    }

    private sealed class ScriptedResolution(Guid[] resolvable) : IWorkspaceResolutionFacade
    {
        public Task<OperationResult<ResolvedWorkspaceServiceModel>> ResolveAsync(
            string userId, ResolveWorkspaceViewModel model, CancellationToken cancellationToken) =>
            throw new NotSupportedException("The worker resolves as the service.");

        public Task<OperationResult<ResolvedWorkspaceServiceModel>> ResolveForOperationAsync(
            Guid workspaceId, Guid membershipId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Reconciliation outlives whoever confirmed the publication.");

        public Task<OperationResult<ResolvedWorkspaceServiceModel>> ResolveForServiceAsync(
            Guid workspaceId, CancellationToken cancellationToken) =>
            Task.FromResult(resolvable.Contains(workspaceId)
                ? OperationResult<ResolvedWorkspaceServiceModel>.Success(
                    new ResolvedWorkspaceServiceModel(
                        workspaceId, "workspace", Guid.NewGuid(), WorkspaceRole.Owner, "service"))
                : OperationResult<ResolvedWorkspaceServiceModel>.Failure(new OperationError(
                    "workspace.not_found", "No such workspace.", new Dictionary<string, string[]>())));
    }
}

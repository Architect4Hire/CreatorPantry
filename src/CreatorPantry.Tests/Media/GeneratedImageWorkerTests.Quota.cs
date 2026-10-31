using CreatorPantry.Domain.Managers.Ai;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Media.Data;
using CreatorPantry.Domain.Modules.Media.Data.Entities;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.AiUsage.Data.Entities;
using CreatorPantry.Domain.Modules.AiUsage.Facade;
using CreatorPantry.Domain.Modules.AiUsage.Managers;
using CreatorPantry.Domain.Modules.Media.Business;
using CreatorPantry.Domain.Modules.Media.Facade;
using CreatorPantry.Domain.Modules.Media.Managers;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using Microsoft.EntityFrameworkCore;
using CreatorPantry.Tests.Brand;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Media;

/// <summary>
/// What a generation costs the account that asked for it (AP.2.5): the hold taken before anything is queued,
/// the charge settled from what the provider actually made, the release of what it did not, and the refusal of
/// a request the allowance will not cover.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The hold is taken in the request scope and settled in the worker's.</strong> That is the whole
/// reason this is worth testing through the real wiring rather than against the quota module directly: the
/// worker resolves a service identity, so if the account were read there instead of carried on the reservation
/// these tests would charge <c>WorkspaceServiceIdentity.AccountId</c> and nobody would notice.
/// </para>
/// <para>
/// Every picture here costs <see cref="ImageQuotaTestSettings.RatePerImage"/> credits, which is what the
/// fixture's configuration rates the fake model at.
/// </para>
/// </remarks>
public sealed partial class GeneratedImageWorkerTests
{
    private const string AccountA = "account-a";

    private const string AccountB = "account-b";

    private static decimal Credits(int pictures) => pictures * ImageQuotaTestSettings.RatePerImage;

    /// <summary>
    /// The ordinary case. Three pictures asked for, three made, three charged — and the hold that was taken
    /// before the request was queued is exactly what is spent, so nothing is left held.
    /// </summary>
    [Fact]
    public async Task Every_picture_the_provider_makes_is_charged_to_the_account_that_asked()
    {
        _generator.Returns(BrandSourceSampleFiles.Png(width: 8, height: 6));

        var requested = await RequestAsync(WorkspaceA, variants: 3, account: AccountA);

        var held = await ReservationAsync(requested.Id);
        Assert.Equal(Credits(3), held.ReservedAmount);
        Assert.Equal(AiQuotaReservationStatus.Held, held.Status);
        Assert.Equal(AccountA, held.AccountId);

        await RunAsync();

        var settled = await ReservationAsync(requested.Id);
        Assert.Equal(AiQuotaReservationStatus.Settled, settled.Status);
        Assert.Equal(Credits(3), settled.SettledAmount);

        var charges = await ChargesAsync(requested.Id);
        Assert.Equal(3, charges.Count);
        Assert.All(charges, charge =>
        {
            Assert.Equal(AccountA, charge.AccountId);
            Assert.Equal(WorkspaceA, charge.WorkspaceId);
            Assert.Equal(AiTaskType.ImageGeneration, charge.TaskType);
            Assert.Equal(AiUsageOutcome.Succeeded, charge.Outcome);
            Assert.True(charge.IsBillable);

            // No tokens, and the flag says so rather than the columns being quietly empty.
            Assert.False(charge.UsageReported);
            Assert.Null(charge.InputTokens);
            Assert.Null(charge.TotalTokens);

            // The ledger names the image operation, never an AI one.
            Assert.Equal(requested.Id, charge.GeneratedImageOperationId);
            Assert.Null(charge.AiOperationId);
        });

        // One per picture, numbered from one: a charge ordinal, not a variant index.
        Assert.Equal([1, 2, 3], charges.Select(charge => charge.AttemptNumber).Order());
    }

    /// <summary>
    /// <strong>A picture this host could not use is still a picture the provider made and billed for.</strong>
    /// Two staged and two corrupt costs four, not two — the inspection that refused them is ours, and charging
    /// only for what we kept would hand an account free pictures for every one of our own refusals.
    /// </summary>
    [Fact]
    public async Task A_picture_the_provider_made_and_nothing_could_be_staged_from_is_still_charged()
    {
        _generator.Returns(BrandSourceSampleFiles.Png(width: 8, height: 6));
        _generator.ReturnsAt(2, BrandSourceSampleFiles.Png()[..12]);
        _generator.ReturnsAt(3, BrandSourceSampleFiles.Png()[..12]);

        var requested = await RequestAsync(WorkspaceA, variants: 4, account: AccountA);

        await RunAsync();

        var operation = await OperationAsync(WorkspaceA, requested.Id);
        Assert.Equal(GeneratedImageOperationStatus.PartiallySucceeded, operation.Status);
        Assert.Equal(4, operation.BilledVariantCount);
        Assert.Equal(2, operation.UnusableVariantCount);
        Assert.Equal(0, operation.FlaggedVariantCount);

        var settled = await ReservationAsync(requested.Id);
        Assert.Equal(Credits(4), settled.SettledAmount);

        var charges = await ChargesAsync(requested.Id);
        Assert.Equal(2, charges.Count(charge => charge.Outcome is AiUsageOutcome.Succeeded));
        Assert.Equal(2, charges.Count(charge => charge.Outcome is AiUsageOutcome.Failed));
    }

    /// <summary>
    /// A picture the provider generated and then withheld is charged, and said in the ledger as what it was
    /// rather than as a plain failure — Venice bills for it either way.
    /// </summary>
    [Fact]
    public async Task A_flagged_picture_is_charged_and_recorded_as_withheld()
    {
        _generator.Returns(BrandSourceSampleFiles.Png(width: 8, height: 6));
        _generator.ThrowsAfter(1, () => new ImageWithheldException("flagged"));

        var requested = await RequestAsync(WorkspaceA, variants: 3, account: AccountA);

        await RunAsync();

        var operation = await OperationAsync(WorkspaceA, requested.Id);
        Assert.Equal(GeneratedImageFailureCategory.ProviderFlagged, operation.FailureCategory);
        Assert.Equal(1, operation.FlaggedVariantCount);

        // The one that arrived and the one that was withheld. The third was never asked for: a flagged
        // picture is terminal, so the pass stops rather than buying the same answer twice more.
        Assert.Equal(2, operation.BilledVariantCount);
        Assert.Equal(Credits(2), (await ReservationAsync(requested.Id)).SettledAmount);

        var charges = await ChargesAsync(requested.Id);
        Assert.Equal(AiUsageOutcome.Succeeded, charges[0].Outcome);
        Assert.Equal(AiUsageOutcome.SafetyBlocked, charges[1].Outcome);
    }

    /// <summary>
    /// <strong>A call that failed before anything was generated is released, not charged.</strong> Nothing was
    /// made, so there is nothing to pay for, and the share of the hold it was holding goes back to the account.
    /// </summary>
    [Fact]
    public async Task A_provider_failure_before_generation_releases_its_share_of_the_hold()
    {
        _generator.Returns(BrandSourceSampleFiles.Png(width: 8, height: 6));
        _generator.ThrowsAfter(2, () => new HttpRequestException(
            "slow down", inner: null, System.Net.HttpStatusCode.TooManyRequests));

        var requested = await RequestAsync(WorkspaceA, variants: 4, account: AccountA);

        // Every attempt hits the same wall, so the operation runs out of attempts and settles on what it got.
        for (var attempt = 0; attempt <= MediaPolicy.GenerationMaxAttempts; attempt++)
        {
            await RunAsync();
            _clock.Advance(TimeSpan.FromHours(1));
            await MaintainAsync();
        }

        var operation = await OperationAsync(WorkspaceA, requested.Id);
        Assert.Equal(2, operation.BilledVariantCount);

        var settled = await ReservationAsync(requested.Id);
        Assert.Equal(Credits(4), settled.ReservedAmount);
        Assert.Equal(Credits(2), settled.SettledAmount);
        Assert.Equal(2, (await ChargesAsync(requested.Id)).Count);
    }

    /// <summary>
    /// A generation whose every call failed before generating owes nothing, and its whole hold is released
    /// rather than left for the expiry sweep to find.
    /// </summary>
    [Fact]
    public async Task A_generation_that_never_reached_the_provider_is_released_rather_than_charged()
    {
        _generator.Throws(() => new HttpRequestException(
            "nope", inner: null, System.Net.HttpStatusCode.BadRequest));

        var requested = await RequestAsync(WorkspaceA, variants: 2, account: AccountA);

        await RunAsync();

        var settled = await ReservationAsync(requested.Id);
        Assert.Equal(AiQuotaReservationStatus.Released, settled.Status);
        Assert.Equal(0m, settled.SettledAmount);
        Assert.Empty(await ChargesAsync(requested.Id));
    }

    /// <summary>
    /// A generation with no hold is never sent to the provider (AP.7.2, PRICE-001). A request made while the host
    /// could not price a picture takes none; if the host can by the time it is claimed, the pictures would otherwise
    /// be made for nothing. Simulated by clearing the hold a request took, which is what such a row looks like.
    /// </summary>
    [Fact]
    public async Task A_generation_with_no_hold_is_refused_before_the_provider()
    {
        var requested = await RequestAsync(WorkspaceA, variants: 2, account: AccountA);

        await using (var scope = _provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().GeneratedImageOperations
                .IgnoreQueryFilters()
                .Where(operation => operation.Id == requested.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(operation => operation.AiQuotaReservationId, (Guid?)null), Ct);
        }

        Assert.Equal(1, (await RunAsync()).Failed);
        Assert.Equal(0, _generator.Calls);

        var failed = await OperationAsync(WorkspaceA, requested.Id);
        Assert.Equal(GeneratedImageFailureCategory.Quota, failed.FailureCategory);
        Assert.Empty(await ChargesAsync(requested.Id));
    }

    /// <summary>
    /// An account switched off while its pictures wait is refused at the claim, as a text run is (AP.7.2): nothing
    /// is made, and the hold settles at nothing.
    /// </summary>
    [Fact]
    public async Task A_generation_whose_account_was_switched_off_while_it_waited_is_not_made()
    {
        var requested = await RequestAsync(WorkspaceA, variants: 2, account: AccountA);

        await using (var scope = _provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
            db.AccountAiQuotas.Add(new AccountAiQuota
            {
                Id = Guid.NewGuid(),
                AccountId = AccountA,
                Unit = AiQuotaUnit.Credits,
                Allowance = null,
                PeriodLength = AiQuotaPeriodLength.Monthly,
                PeriodAnchor = 1,
                TimeZoneId = "Etc/UTC",
                CarryOver = AiQuotaCarryOver.None,
                IsSuspended = true,
                EffectiveFrom = _clock.UtcNow,
                LastChangedAt = _clock.UtcNow,
            });
            await db.SaveChangesAsync(Ct);
        }

        Assert.Equal(1, (await RunAsync()).Failed);
        Assert.Equal(0, _generator.Calls);

        var failed = await OperationAsync(WorkspaceA, requested.Id);
        Assert.Equal(GeneratedImageFailureCategory.Quota, failed.FailureCategory);
        // Said without naming the account's state (DL.11): this summary sits on the operation, and every
        // member of the workspace down to Viewer may read it, so it must not tell a creator's colleagues that
        // their access was switched off.
        Assert.Equal("AI work was not available when these pictures were to be made.", failed.FailureSummary);
        Assert.DoesNotContain("switched off", failed.FailureSummary);
        Assert.DoesNotContain("account", failed.FailureSummary, StringComparison.OrdinalIgnoreCase);

        var settled = await ReservationAsync(requested.Id);
        Assert.NotEqual(AiQuotaReservationStatus.Held, settled.Status);
        Assert.Empty(await ChargesAsync(requested.Id));
    }

    /// <summary>
    /// <strong>A resumed generation charges each picture once.</strong> The first pass stages two and dies
    /// holding its lease; the second regenerates only the variants with no row. Four pictures were made in
    /// total and four are charged — and the charge lands once, when the generation is finally over.
    /// </summary>
    [Fact]
    public async Task A_resumed_generation_charges_every_picture_it_made_exactly_once()
    {
        _generator.Returns(BrandSourceSampleFiles.Png(width: 8, height: 6));

        var requested = await RequestAsync(WorkspaceA, variants: 4, account: AccountA);

        _generator.ThrowsAfter(2, () => new HttpRequestException(
            "unreachable", inner: null, System.Net.HttpStatusCode.ServiceUnavailable));

        await RunAsync();

        // Nothing is settled while there is another pass to come: the hold has to still be there for it.
        var midway = await ReservationAsync(requested.Id);
        Assert.Equal(AiQuotaReservationStatus.Held, midway.Status);
        Assert.Null(midway.SettledAmount);
        Assert.Empty(await ChargesAsync(requested.Id));

        _generator.Throws(null);
        _clock.Advance(TimeSpan.FromHours(1));
        await MaintainAsync();
        await RunAsync();

        var operation = await OperationAsync(WorkspaceA, requested.Id);
        Assert.Equal(GeneratedImageOperationStatus.Succeeded, operation.Status);
        Assert.Equal(4, operation.BilledVariantCount);
        Assert.Equal(4, (await ImagesAsync(WorkspaceA, requested.Id)).Count);

        var settled = await ReservationAsync(requested.Id);
        Assert.Equal(AiQuotaReservationStatus.Settled, settled.Status);
        Assert.Equal(Credits(4), settled.SettledAmount);

        // Four charges, each with its own ordinal: the second pass's take the numbers after the first's.
        Assert.Equal([1, 2, 3, 4], (await ChargesAsync(requested.Id)).Select(c => c.AttemptNumber).Order());
    }

    /// <summary>
    /// An account that cannot pay for the pictures is refused when it asks, with the same problem family a
    /// completion is refused with — and nothing is queued, so no provider is ever called.
    /// </summary>
    [Fact]
    public async Task A_request_the_allowance_will_not_cover_is_refused_and_queues_nothing()
    {
        _generator.Returns(BrandSourceSampleFiles.Png(width: 8, height: 6));

        // An allowance of three pictures, asked for four.
        await SetAllowanceAsync(AccountA, Credits(3));

        await using var scope = ScopeFor(WorkspaceA, account: AccountA);

        var result = await scope.ServiceProvider.GetRequiredService<IGeneratedImageGenerationFacade>()
            .RequestAsync(Request(variants: 4), TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(AiQuotaErrors.Exhausted, result.Error!.Code);
        Assert.Empty(await OperationsAsync(WorkspaceA));
        Assert.Equal(0, _generator.Calls);

        // And what it would have taken is said, so a creator learns why rather than just that (USAGE-007).
        Assert.Equal(Credits(4), Assert.IsType<decimal>(result.Error.Extensions["required"]));
    }

    /// <summary>
    /// <strong>Two accounts, one workspace.</strong> The allowance belongs to the person, not the workspace
    /// they are working in, so one creator spending their month does not stop a colleague generating — and
    /// each one's charges name only their own account.
    /// </summary>
    [Fact]
    public async Task One_accounts_spending_is_not_anothers()
    {
        _generator.Returns(BrandSourceSampleFiles.Png(width: 8, height: 6));

        await SetAllowanceAsync(AccountA, Credits(1));
        await SetAllowanceAsync(AccountB, Credits(4));

        var spent = await RequestAsync(WorkspaceA, variants: 1, account: AccountA);
        await RunAsync();

        // A is now out, in the same workspace B is about to generate in.
        await using (var scope = ScopeFor(WorkspaceA, account: AccountA))
        {
            var refused = await scope.ServiceProvider.GetRequiredService<IGeneratedImageGenerationFacade>()
                .RequestAsync(Request(variants: 1), TestContext.Current.CancellationToken);

            Assert.False(refused.Succeeded);
            Assert.Equal(AiQuotaErrors.Exhausted, refused.Error!.Code);
        }

        var colleague = await RequestAsync(WorkspaceA, variants: 2, account: AccountB);
        await RunAsync();

        Assert.Equal(
            GeneratedImageOperationStatus.Succeeded,
            (await OperationAsync(WorkspaceA, colleague.Id)).Status);

        // Each account is charged for its own pictures and no one else's.
        Assert.All(await ChargesAsync(spent.Id), charge => Assert.Equal(AccountA, charge.AccountId));
        Assert.All(await ChargesAsync(colleague.Id), charge => Assert.Equal(AccountB, charge.AccountId));
        Assert.Equal(Credits(1), await SettledAsync(AccountA));
        Assert.Equal(Credits(2), await SettledAsync(AccountB));
    }

    /// <summary>
    /// A deployment whose pictures this host cannot price will not queue them. A misconfiguration, answered as
    /// one — never as an exhausted allowance, which would tell a creator to wait for a reset that cannot help.
    /// </summary>
    [Fact]
    public async Task A_picture_this_host_cannot_price_is_refused_as_a_provider_problem()
    {
        _generator.Returns(BrandSourceSampleFiles.Png(width: 8, height: 6));

        // The default quality class's model with its rate gone — a table changed after startup checked it.
        // Since AP.3.1 a request is priced at its class, not at the deployment's own defaults, so this is
        // where an unpriceable picture now comes from.
        _provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<AiQuotaOptions>>()
            .Value.ImageRates.Remove(TestDatabase.FakeImageModel);

        await using var scope = ScopeFor(WorkspaceA, account: AccountA);

        var result = await scope.ServiceProvider.GetRequiredService<IGeneratedImageGenerationFacade>()
            .RequestAsync(Request(variants: 1), TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(MediaErrorCodes.GenerationUnpriceable, result.Error!.Code);
        Assert.Empty(await OperationsAsync(WorkspaceA));
        Assert.Equal(0, _generator.Calls);

        // No balance quoted: their allowance is not the problem.
        Assert.Null(result.Error.Extensions);
    }

    /// <summary>
    /// A replayed request is answered with the operation its key already bought, and holds nothing a second
    /// time — the same allowance cannot be reserved twice for one generation.
    /// </summary>
    [Fact]
    public async Task A_replayed_request_finds_the_hold_it_already_took()
    {
        _generator.Returns(BrandSourceSampleFiles.Png(width: 8, height: 6));

        var key = Guid.NewGuid().ToString("N");

        // One scope, so both calls are the same member asking twice. A key found again with anything about
        // the request different — the asker included — is a different request wearing it, and refused.
        await using var scope = ScopeFor(WorkspaceA, account: AccountA);
        var facade = scope.ServiceProvider.GetRequiredService<IGeneratedImageGenerationFacade>();

        var first = await facade.RequestAsync(Request(variants: 2, key: key), TestContext.Current.CancellationToken);
        var replay = await facade.RequestAsync(Request(variants: 2, key: key), TestContext.Current.CancellationToken);

        Assert.True(first.Succeeded, first.Error?.Message);
        Assert.True(replay.Succeeded, replay.Error?.Message);
        Assert.Equal(first.Value!.Id, replay.Value!.Id);
        Assert.Equal(Credits(2), (await ReservationAsync(first.Value.Id)).ReservedAmount);

        // One hold, not two: the same allowance cannot be reserved twice for one generation.
        Assert.Single(await ReservationsAsync(AccountA));
    }

    /// <summary>
    /// <strong>A second request under one key never takes a second hold.</strong> A creator's double click is
    /// answered with the operation the first click bought, and the allowance is held once for it — which is
    /// the ordinary path, and the one that matters most because it is the one that happens.
    /// </summary>
    [Fact]
    public async Task A_second_request_under_one_key_holds_the_allowance_once()
    {
        _generator.Returns(BrandSourceSampleFiles.Png(width: 8, height: 6));

        var key = Guid.NewGuid().ToString("N");

        await using var scope = ScopeFor(WorkspaceA, account: AccountA);
        var facade = scope.ServiceProvider.GetRequiredService<IGeneratedImageGenerationFacade>();

        var first = await facade.RequestAsync(Request(variants: 2, key: key), TestContext.Current.CancellationToken);
        var second = await facade.RequestAsync(Request(variants: 2, key: key), TestContext.Current.CancellationToken);

        Assert.True(first.Succeeded, first.Error?.Message);
        Assert.True(second.Succeeded, second.Error?.Message);
        Assert.Equal(first.Value!.Id, second.Value!.Id);

        var hold = Assert.Single(await ReservationsAsync(AccountA));
        Assert.Equal(AiQuotaReservationStatus.Held, hold.Status);
        Assert.Equal(Credits(2), hold.ReservedAmount);
        Assert.Equal(Credits(2), await OutstandingAsync(AccountA));
    }

    /// <summary>
    /// <strong>A hold whose generation was never queued goes straight back.</strong> The hold has to commit
    /// before the operation row — the period's row version needs its own transaction — so between the two
    /// there is allowance held against nothing, and a request that dies in that window must release it rather
    /// than leave the account looking spent for hours.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Provoked by an operation the database refuses after the hold was taken — here a variant count past
    /// what the table allows, which admission has no opinion about. Any failure in that window behaves the
    /// same way.
    /// </para>
    /// <para>
    /// It also pins the detach that makes the release possible at all: the refused insert is still pending on
    /// the context, and the release is itself a save, so without detaching it the release re-sends the doomed
    /// insert and the hold stays held.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_generation_that_is_never_queued_releases_its_hold_rather_than_stranding_it()
    {
        _generator.Returns(BrandSourceSampleFiles.Png(width: 8, height: 6));

        await using var scope = ScopeFor(WorkspaceA, account: AccountA);
        var dataLayer = scope.ServiceProvider.GetRequiredService<IGeneratedImageGenerationDataLayer>();
        var model = dataLayer.DescribeModel();

        await Assert.ThrowsAnyAsync<Exception>(() => dataLayer.RequestAsync(
            new GeneratedImageOperation
            {
                Id = Guid.NewGuid(),
                WorkspaceId = WorkspaceA,
                Status = GeneratedImageOperationStatus.Requested,
                PromptText = "a bowl of soup on a wooden table",

                // Past MediaPolicy.MaxVariantsPerOperation, so the row is refused by its own check
                // constraint — after the allowance for it has already been held.
                VariantCount = 5,
                IdempotencyKey = Guid.NewGuid().ToString("N"),
                RequestedByMembershipId = Guid.NewGuid(),
                RequestedAt = _clock.UtcNow,
                StatusChangedAt = _clock.UtcNow,
                AvailableAt = _clock.UtcNow,
            },
            new GeneratedImageAdmissionRequest(AccountA, model.ModelName!, model.Resolution, model.Quality),
            TestContext.Current.CancellationToken));

        // The hold was taken and given straight back, so the account has nothing outstanding and no
        // operation to show for it.
        var released = Assert.Single(await ReservationsAsync(AccountA));
        Assert.Equal(AiQuotaReservationStatus.Expired, released.Status);
        Assert.Equal(Credits(5), released.ReservedAmount);
        Assert.Equal(0m, await OutstandingAsync(AccountA));
        Assert.Empty(await OperationsAsync(WorkspaceA));
    }

    /// <summary>
    /// <strong>A generation whose allowance lapsed before a worker reached it makes nothing.</strong> The hold
    /// is taken at the request and never renewed, so a request that waits long enough can be claimed after the
    /// sweep has given its allowance back — and generating then would be generating for free.
    /// </summary>
    [Fact]
    public async Task A_generation_whose_hold_lapsed_is_failed_rather_than_generated_for_nothing()
    {
        _generator.Returns(BrandSourceSampleFiles.Png(width: 8, height: 6));

        var requested = await RequestAsync(WorkspaceA, variants: 2, account: AccountA);

        // Long enough that the hold lapses and the sweep gives it back, which is what a request stuck behind
        // its workspace's running cap looks like.
        _clock.Advance(MediaPolicy.GenerationHoldDuration + TimeSpan.FromMinutes(1));
        await SweepHoldsAsync();

        Assert.Equal(AiQuotaReservationStatus.Expired, (await ReservationAsync(requested.Id)).Status);

        await RunAsync();

        var operation = await OperationAsync(WorkspaceA, requested.Id);
        Assert.Equal(GeneratedImageOperationStatus.Failed, operation.Status);
        Assert.Equal(GeneratedImageFailureCategory.Quota, operation.FailureCategory);

        // And not one provider call was made, so there is nothing anybody owes for.
        Assert.Equal(0, _generator.Calls);
        Assert.Equal(0, operation.BilledVariantCount);
        Assert.Empty(await ChargesAsync(requested.Id));
    }

    /// <summary>
    /// <strong>Past its expiry is lapsed, whether or not the sweep has been (DL.11).</strong> The sweep runs on
    /// an interval, so there is always a window where a hold is past <c>ExpiresAt</c> and still reads
    /// <c>Held</c>. The claim check used to read only the status, so a generation claimed in that window was
    /// judged live, bought its pictures, and then settled against a hold the next sweep expired — pictures
    /// made, nobody charged, and the day's count given back under them.
    /// </summary>
    [Fact]
    public async Task A_generation_claimed_past_its_expiry_makes_nothing_even_before_the_sweep_runs()
    {
        _generator.Returns(BrandSourceSampleFiles.Png(width: 8, height: 6));

        var requested = await RequestAsync(WorkspaceA, variants: 2, account: AccountA);

        // Past the hold's own expiry, and deliberately without sweeping: this is the window.
        _clock.Advance(MediaPolicy.GenerationHoldDuration + TimeSpan.FromMinutes(1));

        // Still Held, which is exactly why the status alone was not enough to judge it by.
        Assert.Equal(AiQuotaReservationStatus.Held, (await ReservationAsync(requested.Id)).Status);

        await RunAsync();

        var operation = await OperationAsync(WorkspaceA, requested.Id);
        Assert.Equal(GeneratedImageOperationStatus.Failed, operation.Status);
        Assert.Equal(GeneratedImageFailureCategory.Quota, operation.FailureCategory);

        Assert.Equal(0, _generator.Calls);
        Assert.Equal(0, operation.BilledVariantCount);
        Assert.Empty(await ChargesAsync(requested.Id));
    }

    /// <summary>One tick before its expiry the same hold is live, so the boundary is the clock and not the sweep.</summary>
    [Fact]
    public async Task A_generation_claimed_one_tick_before_its_expiry_is_made()
    {
        _generator.Returns(BrandSourceSampleFiles.Png(width: 8, height: 6));

        var requested = await RequestAsync(WorkspaceA, variants: 2, account: AccountA);

        _clock.Advance(MediaPolicy.GenerationHoldDuration - TimeSpan.FromTicks(1));

        await RunAsync();

        var operation = await OperationAsync(WorkspaceA, requested.Id);
        Assert.Equal(GeneratedImageOperationStatus.Succeeded, operation.Status);
        Assert.Equal(2, _generator.Calls);
    }

    /// <summary>
    /// <strong>A generation whose last attempt died holding its lease still pays for what it made (DL.7).</strong>
    /// The first pass made two of four and was handed back; the last one died without settling. The sweep ends it
    /// as every other ending is reckoned: the two pictures charged once each, the rest of the hold released, and
    /// no provider called again.
    /// </summary>
    [Fact]
    public async Task A_generation_abandoned_on_its_last_attempt_is_charged_for_the_pictures_it_made()
    {
        _generator.Returns(BrandSourceSampleFiles.Png(width: 8, height: 6));
        _generator.ThrowsAfter(2, () => new HttpRequestException(
            "unreachable", inner: null, System.Net.HttpStatusCode.ServiceUnavailable));

        var requested = await RequestAsync(WorkspaceA, variants: 4, account: AccountA);

        Assert.Equal(1, (await RunAsync()).Requeued);
        Assert.Equal(AiQuotaReservationStatus.Held, (await ReservationAsync(requested.Id)).Status);

        // What the last attempt leaves when its worker dies: still Running, out of attempts, lease lapsed.
        await ForceRunningAsync(requested.Id, MediaPolicy.GenerationMaxAttempts);
        var calls = _generator.Calls;

        var maintenance = await MaintainAsync();

        Assert.Equal(1, maintenance.Abandoned);
        Assert.Equal(0, maintenance.Requeued);
        Assert.Equal(calls, _generator.Calls);

        var operation = await OperationAsync(WorkspaceA, requested.Id);
        Assert.Equal(GeneratedImageOperationStatus.PartiallySucceeded, operation.Status);
        Assert.Equal(GeneratedImageFailureCategory.LeaseAbandoned, operation.FailureCategory);
        Assert.Null(operation.LeasedBy);

        var settled = await ReservationAsync(requested.Id);
        Assert.Equal(AiQuotaReservationStatus.Settled, settled.Status);
        Assert.Equal(Credits(4), settled.ReservedAmount);
        Assert.Equal(Credits(2), settled.SettledAmount);

        var charges = await ChargesAsync(requested.Id);
        Assert.Equal([1, 2], charges.Select(charge => charge.AttemptNumber));
        Assert.All(charges, charge => Assert.Equal(AccountA, charge.AccountId));

        // Ended for good: the next sweep finds nothing, and charges nothing twice.
        Assert.Equal(0, (await MaintainAsync()).Abandoned);
        Assert.Equal(2, (await ChargesAsync(requested.Id)).Count);
    }

    /// <summary>
    /// An abandoned generation whose hold had already lapsed is still ended — uncharged, because a hold the
    /// allowance gave back settles nothing.
    /// </summary>
    [Fact]
    public async Task A_generation_abandoned_after_its_hold_lapsed_is_ended_uncharged()
    {
        _generator.Returns(BrandSourceSampleFiles.Png(width: 8, height: 6));
        _generator.ThrowsAfter(2, () => new HttpRequestException(
            "unreachable", inner: null, System.Net.HttpStatusCode.ServiceUnavailable));

        var requested = await RequestAsync(WorkspaceA, variants: 4, account: AccountA);
        await RunAsync();

        _clock.Advance(MediaPolicy.GenerationHoldDuration + TimeSpan.FromMinutes(1));
        await SweepHoldsAsync();
        await ForceRunningAsync(requested.Id, MediaPolicy.GenerationMaxAttempts);

        Assert.Equal(1, (await MaintainAsync()).Abandoned);

        var operation = await OperationAsync(WorkspaceA, requested.Id);
        Assert.Equal(GeneratedImageOperationStatus.PartiallySucceeded, operation.Status);
        Assert.Equal(GeneratedImageFailureCategory.LeaseAbandoned, operation.FailureCategory);

        Assert.Equal(AiQuotaReservationStatus.Expired, (await ReservationAsync(requested.Id)).Status);
        Assert.Empty(await ChargesAsync(requested.Id));
    }

    /// <summary>
    /// <strong>Two workspaces, two accounts, one sweep.</strong> Each abandoned generation is charged to the account
    /// that asked for it, in the workspace it was made in, for its own pictures only.
    /// </summary>
    [Fact]
    public async Task One_sweep_charges_each_abandoned_generation_to_its_own_account_and_workspace()
    {
        _generator.Returns(BrandSourceSampleFiles.Png(width: 8, height: 6));
        _generator.ThrowsAfter(1, () => new HttpRequestException(
            "unreachable", inner: null, System.Net.HttpStatusCode.ServiceUnavailable));

        var a = await RequestAsync(WorkspaceA, variants: 2, account: AccountA, key: "a-key");
        Assert.Equal(1, (await RunAsync()).Requeued);

        // A is waiting out its backoff, so this pass takes only B.
        _generator.ThrowsAfter(2, () => new HttpRequestException(
            "unreachable", inner: null, System.Net.HttpStatusCode.ServiceUnavailable));

        var b = await RequestAsync(WorkspaceB, variants: 4, account: AccountB, key: "b-key");
        Assert.Equal(1, (await RunAsync()).Requeued);

        await ForceRunningAsync(a.Id, MediaPolicy.GenerationMaxAttempts);
        await ForceRunningAsync(b.Id, MediaPolicy.GenerationMaxAttempts);

        Assert.Equal(2, (await MaintainAsync()).Abandoned);

        var chargedA = await ChargesAsync(a.Id);
        Assert.Single(chargedA);
        Assert.All(chargedA, charge =>
        {
            Assert.Equal(AccountA, charge.AccountId);
            Assert.Equal(WorkspaceA, charge.WorkspaceId);
        });

        var chargedB = await ChargesAsync(b.Id);
        Assert.Equal(2, chargedB.Count);
        Assert.All(chargedB, charge =>
        {
            Assert.Equal(AccountB, charge.AccountId);
            Assert.Equal(WorkspaceB, charge.WorkspaceId);
        });

        Assert.Equal(Credits(1), await SettledAsync(AccountA));
        Assert.Equal(Credits(2), await SettledAsync(AccountB));
    }

    /// <summary>
    /// A settlement resolved into the wrong workspace reads nothing and charges nothing, even with the right
    /// operation and its live lease — what a tampered or stale claim would look like.
    /// </summary>
    [Fact]
    public async Task An_abandoned_generation_cannot_be_settled_from_another_workspace()
    {
        var requested = await RequestAsync(WorkspaceA, variants: 1, account: AccountA);
        var lease = Guid.NewGuid();
        await ClaimAsync(requested.Id, lease);

        await using var scope = ServiceScopeFor(WorkspaceB);

        var outcome = await scope.ServiceProvider.GetRequiredService<IGeneratedImageGenerationFacade>()
            .SettleAbandonedAsync(requested.Id, lease, TestContext.Current.CancellationToken);

        Assert.Equal(GeneratedImageRunOutcome.Skipped, outcome);
        Assert.Equal(GeneratedImageOperationStatus.Running, (await OperationAsync(WorkspaceA, requested.Id)).Status);
        Assert.Equal(AiQuotaReservationStatus.Held, (await ReservationAsync(requested.Id)).Status);
        Assert.Empty(await ChargesAsync(requested.Id));
    }

    /// <summary>"The Worker only" for settling an abandoned generation, as for running one.</summary>
    [Fact]
    public async Task A_person_cannot_settle_an_abandoned_generation_even_holding_its_lease()
    {
        var requested = await RequestAsync(WorkspaceA, variants: 1, account: AccountA);
        var lease = Guid.NewGuid();
        await ClaimAsync(requested.Id, lease);

        await using var scope = ScopeFor(WorkspaceA, account: AccountA);

        var outcome = await scope.ServiceProvider.GetRequiredService<IGeneratedImageGenerationFacade>()
            .SettleAbandonedAsync(requested.Id, lease, TestContext.Current.CancellationToken);

        Assert.Equal(GeneratedImageRunOutcome.Skipped, outcome);
        Assert.Equal(GeneratedImageOperationStatus.Running, (await OperationAsync(WorkspaceA, requested.Id)).Status);
        Assert.Equal(AiQuotaReservationStatus.Held, (await ReservationAsync(requested.Id)).Status);
    }

    /// <summary>
    /// The fallback for one that cannot be settled under its workspace: the sweep's settlement lease ends it
    /// uncharged, and only that lease does — so no sweep leases it forever, and no stale token ends it.
    /// </summary>
    [Fact]
    public async Task An_abandoned_generation_that_cannot_be_settled_is_ended_under_its_settlement_lease_only()
    {
        var requested = await RequestAsync(WorkspaceA, variants: 2, account: AccountA);
        await AbandonAsync(requested.Id, attempts: MediaPolicy.GenerationMaxAttempts);

        await using var scope = _provider.CreateAsyncScope();
        var claims = scope.ServiceProvider.GetRequiredService<GeneratedImageClaimRepository>();

        var recovery = await claims.RecoverAbandonedLeasesAsync(_clock.UtcNow, Ct);
        var claim = Assert.Single(recovery.Exhausted);
        Assert.Equal(WorkspaceA, claim.WorkspaceId);
        Assert.Equal(requested.RequestedAt + MediaPolicy.GenerationHoldDuration, claim.HoldLapsesAt);

        // Leased for settlement, not requeued: no claim pass can take it, so no provider is called for it.
        Assert.Null(await claims.ClaimNextAsync(Guid.NewGuid(), _clock.UtcNow, Ct));
        Assert.Empty((await claims.RecoverAbandonedLeasesAsync(_clock.UtcNow, Ct)).Exhausted);

        Assert.False(await claims.AbandonUnsettledAsync(requested.Id, Guid.NewGuid(), _clock.UtcNow, Ct));
        Assert.True(await claims.AbandonUnsettledAsync(requested.Id, claim.LeaseToken, _clock.UtcNow, Ct));

        var operation = await OperationAsync(WorkspaceA, requested.Id);
        Assert.Equal(GeneratedImageOperationStatus.Failed, operation.Status);
        Assert.Equal(GeneratedImageFailureCategory.LeaseAbandoned, operation.FailureCategory);
        Assert.Null(operation.LeasedBy);
    }

    /// <summary>
    /// <strong>A settlement that fails on a passing error is tried again, not given up.</strong> Ending the
    /// generation uncharged there would forgo two pictures the provider billed for. The settlement lease is left
    /// to lapse, and the next sweep settles it — once.
    /// </summary>
    [Fact]
    public async Task An_abandoned_generation_whose_settlement_fails_is_settled_by_a_later_sweep()
    {
        var requested = await AbandonAfterTwoPicturesAsync();

        _settlementFailures = 1;

        Assert.Equal(0, (await MaintainAsync()).Abandoned);

        var waiting = await OperationAsync(WorkspaceA, requested.Id);
        Assert.Equal(GeneratedImageOperationStatus.Running, waiting.Status);
        Assert.NotNull(waiting.LeasedBy);
        Assert.Equal(AiQuotaReservationStatus.Held, (await ReservationAsync(requested.Id)).Status);
        Assert.Empty(await ChargesAsync(requested.Id));

        _clock.Advance(MediaPolicy.GenerationLeaseDuration + TimeSpan.FromMinutes(1));

        Assert.Equal(1, (await MaintainAsync()).Abandoned);
        Assert.Equal([1, 2], (await ChargesAsync(requested.Id)).Select(charge => charge.AttemptNumber));
        Assert.Equal(Credits(2), (await ReservationAsync(requested.Id)).SettledAmount);
    }

    /// <summary>
    /// The bound on trying again: once the hold has lapsed, settling would charge nothing, so a generation that
    /// still cannot be settled is ended — through the worker's own fallback — rather than leased forever.
    /// </summary>
    [Fact]
    public async Task An_abandoned_generation_that_still_cannot_be_settled_once_its_hold_lapsed_is_ended()
    {
        var requested = await AbandonAfterTwoPicturesAsync();

        _settlementFailures = int.MaxValue;
        _clock.Advance(MediaPolicy.GenerationHoldDuration + TimeSpan.FromMinutes(1));
        await ForceRunningAsync(requested.Id, MediaPolicy.GenerationMaxAttempts);

        Assert.Equal(1, (await MaintainAsync()).Abandoned);

        var operation = await OperationAsync(WorkspaceA, requested.Id);
        Assert.Equal(GeneratedImageOperationStatus.PartiallySucceeded, operation.Status);
        Assert.Equal(GeneratedImageFailureCategory.LeaseAbandoned, operation.FailureCategory);
        Assert.Null(operation.LeasedBy);
        Assert.Empty(await ChargesAsync(requested.Id));
    }

    /// <summary>
    /// A worker that died holding the settlement lease leaves it to lapse like any other: the next sweep hands
    /// the generation out again and it is settled once, and the dead worker's token can no longer settle it.
    /// </summary>
    [Fact]
    public async Task A_settlement_lease_that_lapses_is_handed_out_again_and_settled_once()
    {
        var requested = await AbandonAfterTwoPicturesAsync();

        GeneratedImageSettlementClaim dead;
        await using (var scope = _provider.CreateAsyncScope())
        {
            dead = Assert.Single((await scope.ServiceProvider.GetRequiredService<GeneratedImageClaimRepository>()
                .RecoverAbandonedLeasesAsync(_clock.UtcNow, Ct)).Exhausted);
        }

        _clock.Advance(MediaPolicy.GenerationLeaseDuration + TimeSpan.FromMinutes(1));

        Assert.Equal(1, (await MaintainAsync()).Abandoned);
        Assert.Equal([1, 2], (await ChargesAsync(requested.Id)).Select(charge => charge.AttemptNumber));

        await using (var late = ServiceScopeFor(WorkspaceA))
        {
            var outcome = await late.ServiceProvider.GetRequiredService<IGeneratedImageGenerationFacade>()
                .SettleAbandonedAsync(dead.OperationId, dead.LeaseToken, Ct);

            Assert.Equal(GeneratedImageRunOutcome.Skipped, outcome);
        }

        Assert.Equal(2, (await ChargesAsync(requested.Id)).Count);
    }

    /// <summary>
    /// A generation that made two of four pictures, was handed back, and whose last attempt then died holding its
    /// lease: Running, out of attempts, lease lapsed, two pictures billed and held for.
    /// </summary>
    private async Task<GeneratedImageOperationServiceModel> AbandonAfterTwoPicturesAsync()
    {
        _generator.Returns(BrandSourceSampleFiles.Png(width: 8, height: 6));
        _generator.ThrowsAfter(2, () => new HttpRequestException(
            "unreachable", inner: null, System.Net.HttpStatusCode.ServiceUnavailable));

        var requested = await RequestAsync(WorkspaceA, variants: 4, account: AccountA);
        Assert.Equal(1, (await RunAsync()).Requeued);

        await ForceRunningAsync(requested.Id, MediaPolicy.GenerationMaxAttempts);

        return requested;
    }

    /// <summary>How many settlements the quota facade fails before it lets one through.</summary>
    private int _settlementFailures;

    /// <summary>
    /// The real quota facade, except that a settlement throws a passing error while
    /// <see cref="_settlementFailures"/> is above nought — what a timeout or deadlock looks like to the worker.
    /// </summary>
    public class FailingSettlement : System.Reflection.DispatchProxy
    {
        private IAiQuotaAdmissionFacade _inner = null!;
        private GeneratedImageWorkerTests _owner = null!;

        internal static IAiQuotaAdmissionFacade Wrap(IAiQuotaAdmissionFacade inner, GeneratedImageWorkerTests owner)
        {
            var proxy = Create<IAiQuotaAdmissionFacade, FailingSettlement>();
            var failing = (FailingSettlement)(object)proxy;
            failing._inner = inner;
            failing._owner = owner;

            return proxy;
        }

        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);

            if (targetMethod.Name == nameof(IAiQuotaAdmissionFacade.StageImageUsageAsync)
                && _owner._settlementFailures > 0)
            {
                _owner._settlementFailures--;

                return Task.FromException(new TimeoutException("A passing error."));
            }

            try
            {
                return targetMethod.Invoke(_inner, args);
            }
            catch (System.Reflection.TargetInvocationException exception) when (exception.InnerException is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception.InnerException).Throw();

                throw;
            }
        }
    }

    /// <summary>Runs the allowance maintenance sweep, which releases holds whose time is up.</summary>
    private async Task SweepHoldsAsync()
    {
        await using var scope = _provider.CreateAsyncScope();

        await scope.ServiceProvider.GetRequiredService<IAiQuotaMaintenanceFacade>()
            .SweepAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>What the account's period still has held against it, settled or not.</summary>
    private async Task<decimal> OutstandingAsync(string accountId)
    {
        await using var scope = _provider.CreateAsyncScope();

        return (await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
                .AccountAiQuotaPeriods
                .AsNoTracking()
                .Where(period => period.AccountId == accountId)
                .ToListAsync(TestContext.Current.CancellationToken))
            .Sum(period => period.Reserved);
    }

    /// <summary>The hold a generation takes, read straight through.</summary>
    private async Task<AccountAiQuotaReservation> ReservationAsync(Guid operationId)
    {
        await using var scope = _provider.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .AccountAiQuotaReservations
            .AsNoTracking()
            .SingleAsync(
                reservation => reservation.AiOperationId == operationId,
                TestContext.Current.CancellationToken);
    }

    private async Task<IReadOnlyList<AccountAiQuotaReservation>> ReservationsAsync(string accountId)
    {
        await using var scope = _provider.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .AccountAiQuotaReservations
            .AsNoTracking()
            .Where(reservation => reservation.AccountId == accountId)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>One generation's charges, in the order their ordinals were handed out.</summary>
    private async Task<IReadOnlyList<AccountAiUsageEntry>> ChargesAsync(Guid operationId)
    {
        await using var scope = _provider.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .AccountAiUsageEntries
            .AsNoTracking()
            .Where(entry => entry.GeneratedImageOperationId == operationId)
            .OrderBy(entry => entry.AttemptNumber)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>What one account has actually spent, across every generation.</summary>
    private async Task<decimal> SettledAsync(string accountId) =>
        (await ReservationsAsync(accountId)).Sum(reservation => reservation.SettledAmount ?? 0m);

    /// <summary>
    /// Gives one account a quota of its own, so a test can run it out without touching anyone else's.
    /// </summary>
    private async Task SetAllowanceAsync(string accountId, decimal allowance)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        db.AccountAiQuotas.Add(new AccountAiQuota
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Allowance = allowance,
            Unit = AiQuotaUnit.Credits,
            PeriodLength = AiQuotaPeriodLength.Monthly,
            PeriodAnchor = 1,
            TimeZoneId = "Etc/UTC",
            CarryOver = AiQuotaCarryOver.None,
            IsSuspended = false,
            EffectiveFrom = _clock.UtcNow,
            LastChangedAt = _clock.UtcNow,
        });

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }
}

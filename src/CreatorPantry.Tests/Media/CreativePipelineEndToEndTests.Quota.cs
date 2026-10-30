using System.Net;
using System.Net.Http.Json;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.AiUsage.Data.Entities;
using CreatorPantry.Domain.Modules.AiUsage.Managers;
using CreatorPantry.Domain.Modules.Auth.Data.Entities;
using CreatorPantry.Tests.Brand;
using CreatorPantry.Tests.Tenancy;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Media;

/// <summary>
/// Where the pipeline stops when the creator cannot pay for the pictures (AP.2.5).
/// </summary>
/// <remarks>
/// The pipeline's own test above is about a picture going all the way to a recipe and back off it. This is the
/// step before all of that: the allowance, which is checked before an operation exists at all, so a refusal
/// leaves the pipeline exactly as it was rather than part-way through.
/// </remarks>
public sealed partial class CreativePipelineEndToEndTests
{
    /// <summary>
    /// A request the account's allowance will not cover is refused before anything is queued — so the
    /// pipeline has nothing half-done in it, and the next request with a real allowance behaves as though the
    /// refused one never happened.
    /// </summary>
    [Fact]
    public async Task A_pipeline_request_past_the_allowance_is_refused_and_leaves_nothing_behind()
    {
        var workspace = _fixture.WorkspaceA;
        using var client = await _fixture.SignInAsync(workspace.OwnerEmail, cancellationToken: Ct);

        // One picture's worth of allowance, against a request for three.
        await AllowanceAsync(workspace.OwnerEmail, TestDatabase.FakeImageRate);

        var refused = await client.PostAsJsonAsync(
            In(workspace, "generated-images"),
            new { promptText = "Three plates of soda bread.", variantCount = 3 },
            "pipeline-over-allowance",
            Ct);

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Equal(AiQuotaErrors.Exhausted, (await BodyOf(refused)).GetProperty("code").GetString());

        // Nothing queued, nothing generated, and no hold left stranding the allowance it did not spend.
        Assert.Equal(0, (await RunWorkerAsync()).Claimed);
        Assert.Equal(0, _generator.Calls);
        Assert.Empty(await ReservationsAsync(workspace.OwnerEmail));

        // And the one picture they can afford still works, which is what proves the refusal wrote nothing.
        var afforded = await client.PostAsJsonAsync(
            In(workspace, "generated-images"),
            new { promptText = "One plate of soda bread.", variantCount = 1 },
            "pipeline-within-allowance",
            Ct);

        Assert.Equal(HttpStatusCode.Accepted, afforded.StatusCode);
        await RunWorkerAsync();

        var operationId = (await BodyOf(afforded)).GetProperty("id").GetGuid();
        var made = await BodyOf(await client.GetAsync(In(workspace, $"generated-images/operations/{operationId}"), Ct));
        Assert.Equal("Succeeded", made.GetProperty("status").GetString());

        var settled = Assert.Single(await ReservationsAsync(workspace.OwnerEmail));
        Assert.Equal(AiQuotaReservationStatus.Settled, settled.Status);
        Assert.Equal(TestDatabase.FakeImageRate, settled.SettledAmount);
    }

    /// <summary>
    /// <strong>A pipeline run that could use only some of its pictures is charged for each one made (DL.7).</strong>
    /// Two arrive whole and two unreadable: the provider made four, so four are charged, once each, and the ledger
    /// says which two were useless.
    /// </summary>
    [Fact]
    public async Task A_pipeline_run_that_could_use_only_some_of_its_pictures_is_charged_for_each_one_made()
    {
        var workspace = _fixture.WorkspaceA;
        _generator.Returns(BrandSourceSampleFiles.Png(width: 8, height: 6));
        _generator.ReturnsAt(2, BrandSourceSampleFiles.Png()[..12]);
        _generator.ReturnsAt(3, BrandSourceSampleFiles.Png()[..12]);

        using var client = await _fixture.SignInAsync(workspace.OwnerEmail, cancellationToken: Ct);

        var asked = await client.PostAsJsonAsync(
            In(workspace, "generated-images"),
            new { promptText = "Four plates of soda bread.", variantCount = 4 },
            "pipeline-partial",
            Ct);
        Assert.Equal(HttpStatusCode.Accepted, asked.StatusCode);
        await RunWorkerAsync();

        var operationId = (await BodyOf(asked)).GetProperty("id").GetGuid();
        var made = await BodyOf(await client.GetAsync(In(workspace, $"generated-images/operations/{operationId}"), Ct));
        Assert.Equal("PartiallySucceeded", made.GetProperty("status").GetString());
        Assert.Equal(2, made.GetProperty("images").GetArrayLength());

        var settled = Assert.Single(await ReservationsAsync(workspace.OwnerEmail));
        Assert.Equal(AiQuotaReservationStatus.Settled, settled.Status);
        Assert.Equal(4 * TestDatabase.FakeImageRate, settled.SettledAmount);

        var charges = await ChargesAsync(operationId);
        Assert.Equal([1, 2, 3, 4], charges.Select(charge => charge.AttemptNumber));
        Assert.Equal(2, charges.Count(charge => charge.Outcome is AiUsageOutcome.Succeeded));
        Assert.Equal(2, charges.Count(charge => charge.Outcome is AiUsageOutcome.Failed));
    }

    /// <summary>
    /// <strong>An allowance is the person's (DL.7).</strong> The owner of workspace A runs out; a colleague in the
    /// same workspace and the owner of workspace B both still make pictures, and each is charged for their own and
    /// nobody else's.
    /// </summary>
    [Fact]
    public async Task One_accounts_spent_allowance_stops_no_other_account_in_either_workspace()
    {
        var a = _fixture.WorkspaceA;
        var b = _fixture.WorkspaceB;
        _generator.Returns(BrandSourceSampleFiles.Png(width: 8, height: 6));

        await AllowanceAsync(a.OwnerEmail, TestDatabase.FakeImageRate);
        await AllowanceAsync(a.MemberEmail, 2 * TestDatabase.FakeImageRate);
        await AllowanceAsync(b.OwnerEmail, 2 * TestDatabase.FakeImageRate);

        using var owner = await _fixture.SignInAsync(a.OwnerEmail, cancellationToken: Ct);

        Assert.Equal(HttpStatusCode.Accepted, (await owner.PostAsJsonAsync(
            In(a, "generated-images"),
            new { promptText = "One plate of soda bread.", variantCount = 1 },
            "pipeline-owner-spends",
            Ct)).StatusCode);
        await RunWorkerAsync();

        Assert.Equal(HttpStatusCode.TooManyRequests, (await owner.PostAsJsonAsync(
            In(a, "generated-images"),
            new { promptText = "Another plate of soda bread.", variantCount = 1 },
            "pipeline-owner-out",
            Ct)).StatusCode);

        using var colleague = await _fixture.SignInAsync(a.MemberEmail, cancellationToken: Ct);
        using var neighbour = await _fixture.SignInAsync(b.OwnerEmail, cancellationToken: Ct);

        var theirs = await colleague.PostAsJsonAsync(
            In(a, "generated-images"),
            new { promptText = "Two plates of soda bread.", variantCount = 2 },
            "pipeline-colleague",
            Ct);
        var neighbours = await neighbour.PostAsJsonAsync(
            In(b, "generated-images"),
            new { promptText = "Two loaves on a rack.", variantCount = 2 },
            "pipeline-neighbour",
            Ct);

        Assert.Equal(HttpStatusCode.Accepted, theirs.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, neighbours.StatusCode);
        await RunWorkerAsync();

        Assert.Equal(TestDatabase.FakeImageRate, await SettledAsync(a.OwnerEmail));
        Assert.Equal(2 * TestDatabase.FakeImageRate, await SettledAsync(a.MemberEmail));
        Assert.Equal(2 * TestDatabase.FakeImageRate, await SettledAsync(b.OwnerEmail));

        var colleagueAccount = await AccountAsync(a.MemberEmail);
        var neighbourAccount = await AccountAsync(b.OwnerEmail);

        // Counted first, so an empty ledger cannot pass the per-charge checks below.
        var colleagueCharges = await ChargesAsync((await BodyOf(theirs)).GetProperty("id").GetGuid());
        var neighbourCharges = await ChargesAsync((await BodyOf(neighbours)).GetProperty("id").GetGuid());
        Assert.Equal(2, colleagueCharges.Count);
        Assert.Equal(2, neighbourCharges.Count);

        Assert.All(colleagueCharges, charge =>
        {
            Assert.Equal(colleagueAccount, charge.AccountId);
            Assert.Equal(a.Id, charge.WorkspaceId);
        });
        Assert.All(
            neighbourCharges,
            charge =>
            {
                Assert.Equal(neighbourAccount, charge.AccountId);
                Assert.Equal(b.Id, charge.WorkspaceId);
            });
    }

    private async Task<decimal> SettledAsync(string email) =>
        (await ReservationsAsync(email)).Sum(reservation => reservation.SettledAmount ?? 0m);

    private async Task<IReadOnlyList<AccountAiUsageEntry>> ChargesAsync(Guid operationId)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .AccountAiUsageEntries
            .AsNoTracking()
            .Where(entry => entry.GeneratedImageOperationId == operationId)
            .OrderBy(entry => entry.AttemptNumber)
            .ToListAsync(Ct);
    }

    private async Task<string> AccountAsync(string email)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();

        return (await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>()
            .FindByEmailAsync(email))!.Id;
    }

    private async Task AllowanceAsync(string email, decimal allowance)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        db.AccountAiQuotas.Add(new AccountAiQuota
        {
            Id = Guid.NewGuid(),
            AccountId = (await users.FindByEmailAsync(email))!.Id,
            Allowance = allowance,
            Unit = AiQuotaUnit.Credits,
            PeriodLength = AiQuotaPeriodLength.Monthly,
            PeriodAnchor = 1,
            TimeZoneId = "Etc/UTC",
            CarryOver = AiQuotaCarryOver.None,
            IsSuspended = false,
            EffectiveFrom = DateTimeOffset.UtcNow.AddDays(-1),
            LastChangedAt = DateTimeOffset.UtcNow.AddDays(-1),
        });

        await db.SaveChangesAsync(Ct);
    }

    private async Task<IReadOnlyList<AccountAiQuotaReservation>> ReservationsAsync(string email)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var accountId = (await users.FindByEmailAsync(email))!.Id;

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .AccountAiQuotaReservations
            .AsNoTracking()
            .Where(reservation => reservation.AccountId == accountId)
            .ToListAsync(Ct);
    }
}

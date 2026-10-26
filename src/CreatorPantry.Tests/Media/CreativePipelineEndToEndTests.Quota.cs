using System.Net;
using System.Net.Http.Json;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.AiUsage.Data.Entities;
using CreatorPantry.Domain.Modules.AiUsage.Managers;
using CreatorPantry.Domain.Modules.Auth.Data.Entities;
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

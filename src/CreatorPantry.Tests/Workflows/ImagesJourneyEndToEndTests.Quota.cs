using System.Net;
using System.Net.Http.Json;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.AiUsage.Data.Entities;
using CreatorPantry.Domain.Modules.Auth.Data.Entities;
using CreatorPantry.Domain.Modules.AiUsage.Managers;
using CreatorPantry.Tests.Brand;
using CreatorPantry.Tests.Tenancy;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Workflows;

/// <summary>
/// What a picture costs, through the whole stack (AP.2.5): the browser's own cookie session, the Gateway, the
/// API, the queue and the worker — with a real allowance in front of all of it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Worth doing here as well as against the module.</strong> The hold is taken in the request scope
/// and settled in the worker's, and only a hosted test proves the two halves meet: the worker resolves a
/// service identity, so an account read there rather than from the reservation would charge nobody in
/// particular and every module-level test would still pass.
/// </para>
/// <para>
/// The fake model is rated by <see cref="TestDatabase"/>, which every host here is configured by, so one
/// picture costs <see cref="TestDatabase.FakeImageRate"/> credits.
/// </para>
/// </remarks>
public sealed partial class ImagesJourneyEndToEndTests
{
    private static decimal Credits(int pictures) => pictures * TestDatabase.FakeImageRate;

    /// <summary>
    /// A creator who cannot pay for the pictures is turned away when they ask — a 429 carrying their own
    /// figures — and nothing is queued, so no provider is ever called and there is nothing to clean up.
    /// </summary>
    [Fact]
    public async Task A_request_past_the_accounts_allowance_is_refused_at_the_edge_and_queues_nothing()
    {
        _generator.Returns(BrandSourceSampleFiles.Png(width: 8, height: 6));

        // Enough for three pictures, asked for four.
        await AllowanceAsync(A.OwnerEmail, Credits(3));

        using var owner = await _fixture.SignInAsync(A.OwnerEmail, cancellationToken: Ct);

        var refused = await owner.PostAsJsonAsync(
            In(A, "generated-images"),
            new { promptText = Prompt, variantCount = 4 },
            Guid.NewGuid().ToString("N"),
            Ct);

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);

        var problem = await BodyOf(refused);
        Assert.Equal(AiQuotaErrors.Exhausted, problem.GetProperty("code").GetString());

        // The four things a refused caller is owed (USAGE-007), and not one of them names a workspace.
        Assert.Equal("Credits", problem.GetProperty("unit").GetString());
        Assert.Equal(Credits(4), problem.GetProperty("required").GetDecimal());
        Assert.True(problem.TryGetProperty("resetsAt", out _));
        Assert.True(problem.TryGetProperty("remaining", out _));

        // Nothing queued and nothing generated: the refusal happened before either could.
        Assert.Equal(0, (await RunWorkerAsync()).Claimed);
        Assert.Equal(0, _generator.Calls);
    }

    /// <summary>
    /// <strong>At zero, a picture waits (DL.7).</strong> A creator who has spent the last of their allowance on
    /// pictures asks for one more: refused before anything is queued, with nothing left said as nothing, and no
    /// provider called. A picture is never free because the balance happened to be empty.
    /// </summary>
    [Fact]
    public async Task A_picture_asked_for_at_an_allowance_of_nothing_is_refused_before_it_is_queued()
    {
        _generator.Returns(BrandSourceSampleFiles.Png(width: 8, height: 6));

        await AllowanceAsync(A.OwnerEmail, Credits(1));

        using var owner = await _fixture.SignInAsync(A.OwnerEmail, cancellationToken: Ct);

        var last = await owner.PostAsJsonAsync(
            In(A, "generated-images"),
            new { promptText = Prompt, variantCount = 1 },
            Guid.NewGuid().ToString("N"),
            Ct);
        Assert.Equal(HttpStatusCode.Accepted, last.StatusCode);
        await RunWorkerAsync();

        // Spent to the credit: what is left is nothing, not a remainder.
        Assert.Equal(Credits(1), await SpentAsync(A.OwnerEmail));
        var calls = _generator.Calls;

        var refused = await owner.PostAsJsonAsync(
            In(A, "generated-images"),
            new { promptText = Prompt, variantCount = 1 },
            Guid.NewGuid().ToString("N"),
            Ct);

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);

        var problem = await BodyOf(refused);
        Assert.Equal(AiQuotaErrors.Exhausted, problem.GetProperty("code").GetString());
        Assert.Equal(0m, problem.GetProperty("remaining").GetDecimal());
        Assert.Equal(Credits(1), problem.GetProperty("required").GetDecimal());

        Assert.Equal(0, (await RunWorkerAsync()).Claimed);
        Assert.Equal(calls, _generator.Calls);
    }

    /// <summary>
    /// <strong>A pass that could only use some of what it generated still pays for all of it.</strong> Two
    /// pictures arrive whole and two arrive unreadable; the provider made four and charges for four, and the
    /// creator gets the two that are real.
    /// </summary>
    [Fact]
    public async Task A_partial_pass_charges_every_picture_the_provider_made()
    {
        _generator.Returns(BrandSourceSampleFiles.Png(width: 8, height: 6));
        _generator.ReturnsAt(2, BrandSourceSampleFiles.Png()[..12]);
        _generator.ReturnsAt(3, BrandSourceSampleFiles.Png()[..12]);

        using var owner = await _fixture.SignInAsync(A.OwnerEmail, cancellationToken: Ct);

        var asked = await owner.PostAsJsonAsync(
            In(A, "generated-images"),
            new { promptText = Prompt, variantCount = 4 },
            Guid.NewGuid().ToString("N"),
            Ct);

        Assert.Equal(HttpStatusCode.Accepted, asked.StatusCode);

        var operationId = (await BodyOf(asked)).GetProperty("id").GetGuid();
        await RunWorkerAsync();

        var made = await BodyOf(await owner.GetAsync(In(A, $"generated-images/operations/{operationId}"), Ct));
        Assert.Equal("PartiallySucceeded", made.GetProperty("status").GetString());
        Assert.Equal(2, made.GetProperty("images").GetArrayLength());

        // Charged for four, and the ledger says which two were useless rather than reporting four successes.
        Assert.Equal(Credits(4), await SpentAsync(A.OwnerEmail));

        var charges = await ChargesAsync(operationId);
        Assert.Equal(4, charges.Count);
        Assert.Equal(2, charges.Count(charge => charge.Outcome is AiUsageOutcome.Succeeded));
        Assert.Equal(2, charges.Count(charge => charge.Outcome is AiUsageOutcome.Failed));
    }

    /// <summary>
    /// <strong>The allowance belongs to the person, not the workspace.</strong> Two members of one workspace
    /// each have their own: the owner spending theirs does not stop their colleague generating, and neither
    /// one's pictures are charged to the other.
    /// </summary>
    [Fact]
    public async Task One_members_spent_allowance_does_not_stop_a_colleague_in_the_same_workspace()
    {
        _generator.Returns(BrandSourceSampleFiles.Png(width: 8, height: 6));

        await AllowanceAsync(A.OwnerEmail, Credits(1));
        await AllowanceAsync(A.MemberEmail, Credits(2));

        using var owner = await _fixture.SignInAsync(A.OwnerEmail, cancellationToken: Ct);

        var spent = await owner.PostAsJsonAsync(
            In(A, "generated-images"),
            new { promptText = Prompt, variantCount = 1 },
            Guid.NewGuid().ToString("N"),
            Ct);
        Assert.Equal(HttpStatusCode.Accepted, spent.StatusCode);
        await RunWorkerAsync();

        // The owner is now out, in the workspace their colleague is about to generate in.
        var again = await owner.PostAsJsonAsync(
            In(A, "generated-images"),
            new { promptText = Prompt, variantCount = 1 },
            Guid.NewGuid().ToString("N"),
            Ct);
        Assert.Equal(HttpStatusCode.TooManyRequests, again.StatusCode);

        using var colleague = await _fixture.SignInAsync(A.MemberEmail, cancellationToken: Ct);

        var theirs = await colleague.PostAsJsonAsync(
            In(A, "generated-images"),
            new { promptText = Prompt, variantCount = 2 },
            Guid.NewGuid().ToString("N"),
            Ct);

        Assert.Equal(HttpStatusCode.Accepted, theirs.StatusCode);
        await RunWorkerAsync();

        var operationId = (await BodyOf(theirs)).GetProperty("id").GetGuid();
        var made = await BodyOf(await colleague.GetAsync(In(A, $"generated-images/operations/{operationId}"), Ct));
        Assert.Equal("Succeeded", made.GetProperty("status").GetString());

        // Each paid for their own and nobody paid for anyone else's.
        Assert.Equal(Credits(1), await SpentAsync(A.OwnerEmail));
        Assert.Equal(Credits(2), await SpentAsync(A.MemberEmail));
    }

    /// <summary>Gives one person an allowance of their own, so a test can run it out.</summary>
    private async Task AllowanceAsync(string email, decimal allowance)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        db.AccountAiQuotas.Add(new AccountAiQuota
        {
            Id = Guid.NewGuid(),
            AccountId = await AccountAsync(email),
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

    /// <summary>What one person has actually been charged, across every generation they asked for.</summary>
    private async Task<decimal> SpentAsync(string email)
    {
        var accountId = await AccountAsync(email);

        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .AccountAiQuotaReservations
            .AsNoTracking()
            .Where(reservation => reservation.AccountId == accountId)
            .SumAsync(reservation => reservation.SettledAmount ?? 0m, Ct);
    }

    private async Task<IReadOnlyList<AccountAiUsageEntry>> ChargesAsync(Guid operationId)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .AccountAiUsageEntries
            .AsNoTracking()
            .Where(entry => entry.GeneratedImageOperationId == operationId)
            .ToListAsync(Ct);
    }

    /// <summary>The Identity account behind a seeded sign-in, which is what an allowance belongs to.</summary>
    private async Task<string> AccountAsync(string email)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        return (await users.FindByEmailAsync(email))!.Id;
    }
}

using System.Net;
using System.Net.Http.Json;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.AiUsage.Data.Entities;
using CreatorPantry.Domain.Modules.AiUsage.Managers;
using CreatorPantry.Tests.Brand;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CreatorPantry.Tests.Workflows;

/// <summary>
/// The platform's daily image spend breaker through the whole stack (PRICE-005, AP.2.7): the browser's own
/// session, the Gateway and the API — and what a creator is told when it has tripped.
/// </summary>
/// <remarks>
/// The day is put at its ceiling directly rather than spent there, because the fake picture costs under a cent
/// and the ceiling is the API's own configured figure; how the counter reaches it is
/// <c>PlatformImageSpendAdmissionTests</c> and <c>PlatformImageSpendConcurrencyTests</c>.
/// </remarks>
public sealed partial class ImagesJourneyEndToEndTests
{
    /// <summary>
    /// <strong>Refused, said plainly, and nothing about anybody's spending.</strong> A 503 with its own code
    /// and <c>retry: later</c>; no unit, no remaining, no required, no reset and no <c>Retry-After</c> — the
    /// creator's allowance is not the reason and every figure behind the real one is other people's. Nothing is
    /// held against their allowance and nothing is queued.
    /// </summary>
    [Fact]
    public async Task A_request_past_the_platform_ceiling_is_refused_without_figures_and_queues_nothing()
    {
        _generator.Returns(BrandSourceSampleFiles.Png(width: 8, height: 6));
        await AtCeilingAsync();

        using var owner = await _fixture.SignInAsync(A.OwnerEmail, cancellationToken: Ct);

        var refused = await owner.PostAsJsonAsync(
            In(A, "generated-images"),
            new { promptText = Prompt, variantCount = 1 },
            Guid.NewGuid().ToString("N"),
            Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        Assert.False(refused.Headers.Contains("Retry-After"));

        var problem = await BodyOf(refused);
        Assert.Equal(AiQuotaErrors.CapacityUnavailable, problem.GetProperty("code").GetString());
        Assert.Equal("later", problem.GetProperty("retry").GetString());

        foreach (var figure in new[] { "unit", "remaining", "required", "resetsAt", "ceiling", "admitted" })
        {
            Assert.False(problem.TryGetProperty(figure, out _), $"The refusal carried '{figure}'.");
        }

        // Nothing held against the creator, nothing queued, nothing generated.
        Assert.Equal(0m, await HeldAsync());
        Assert.Equal(0, (await RunWorkerAsync()).Claimed);
        Assert.Equal(0, _generator.Calls);

        // And the day is marked tripped, which is what raised the alert.
        Assert.NotNull((await TodayAsync())!.TrippedAt);
    }

    /// <summary>
    /// <strong>Everybody is told the same thing, and the way back is the same request.</strong> Two members of
    /// the workspace, each with their own allowance untouched, get identical refusals. Once the day is lifted,
    /// the owner's retry with the key it was refused under — which is what the browser sends — is admitted as one
    /// request, because a refused key bought nothing and was never stored.
    /// </summary>
    [Fact]
    public async Task Every_member_is_refused_alike_and_a_retry_with_the_same_key_succeeds_once_lifted()
    {
        _generator.Returns(BrandSourceSampleFiles.Png(width: 8, height: 6));
        await AtCeilingAsync();

        using var owner = await _fixture.SignInAsync(A.OwnerEmail, cancellationToken: Ct);
        using var member = await _fixture.SignInAsync(A.MemberEmail, cancellationToken: Ct);
        var key = Guid.NewGuid().ToString("N");

        var ownerRefused = await owner.PostAsJsonAsync(
            In(A, "generated-images"), new { promptText = Prompt, variantCount = 1 }, key, Ct);
        var memberRefused = await member.PostAsJsonAsync(
            In(A, "generated-images"), new { promptText = Prompt, variantCount = 1 }, Guid.NewGuid().ToString("N"), Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, ownerRefused.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, memberRefused.StatusCode);

        // The same members, apart from the request's own trace: nothing in either says whose spending it was.
        static string[] Shape(System.Text.Json.JsonElement body) =>
            [.. body.EnumerateObject().Select(property => property.Name).Where(name => name != "traceId").Order()];

        var ownerBody = await BodyOf(ownerRefused);
        var memberBody = await BodyOf(memberRefused);
        Assert.Equal(Shape(ownerBody), Shape(memberBody));
        Assert.Equal(ownerBody.GetProperty("title").GetString(), memberBody.GetProperty("title").GetString());

        await LiftTodayAsync();

        var retried = await owner.PostAsJsonAsync(
            In(A, "generated-images"), new { promptText = Prompt, variantCount = 1 }, key, Ct);

        Assert.Equal(HttpStatusCode.Accepted, retried.StatusCode);
        Assert.Equal(1, (await RunWorkerAsync()).Claimed);
    }

    /// <summary>An admitted request is counted at the fake model's list price, two pictures of it.</summary>
    [Fact]
    public async Task An_admitted_request_counts_its_list_price_against_the_day()
    {
        _generator.Returns(BrandSourceSampleFiles.Png(width: 8, height: 6));

        using var owner = await _fixture.SignInAsync(A.OwnerEmail, cancellationToken: Ct);

        var asked = await owner.PostAsJsonAsync(
            In(A, "generated-images"),
            new { promptText = Prompt, variantCount = 2 },
            Guid.NewGuid().ToString("N"),
            Ct);

        Assert.Equal(HttpStatusCode.Accepted, asked.StatusCode);

        // 0.008 a picture under Ai:Cost:Images (TestDatabase), two of them.
        Assert.Equal(16_000, (await TodayAsync())!.AdmittedMicros);
    }

    /// <summary>Puts today's counter exactly on the API's configured ceiling, so the next picture would pass it.</summary>
    private async Task AtCeilingAsync()
    {
        var services = _fixture.Api.Factory.Services;
        var ceiling = services.GetRequiredService<IOptions<AiQuotaOptions>>().Value.PlatformImageSpendPerDay!.Value;

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        db.PlatformImageSpendDays.Add(new PlatformImageSpendDay
        {
            Day = PlatformImageSpendPolicy.DayOf(services.GetRequiredService<IClock>().UtcNow),
            AdmittedMicros = PlatformImageSpendPolicy.ToMicros(ceiling),
        });

        await db.SaveChangesAsync(Ct);
    }

    /// <summary>Lifts today as the ops route does, without the route: the route itself is <c>OpsPlatformImageSpendTests</c>.</summary>
    private async Task LiftTodayAsync()
    {
        var services = _fixture.Api.Factory.Services;
        var today = PlatformImageSpendPolicy.DayOf(services.GetRequiredService<IClock>().UtcNow);

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        var row = await db.PlatformImageSpendDays.SingleAsync(spend => spend.Day == today, Ct);
        row.LiftedAt = DateTimeOffset.UtcNow;
        row.LiftedBy = "tests";

        await db.SaveChangesAsync(Ct);
    }

    private async Task<PlatformImageSpendDay?> TodayAsync()
    {
        var services = _fixture.Api.Factory.Services;
        var today = PlatformImageSpendPolicy.DayOf(services.GetRequiredService<IClock>().UtcNow);

        await using var scope = services.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .PlatformImageSpendDays.AsNoTracking().SingleOrDefaultAsync(row => row.Day == today, Ct);
    }

    private async Task<decimal> HeldAsync()
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .AccountAiQuotaReservations.AsNoTracking().SumAsync(reservation => reservation.ReservedAmount, Ct);
    }
}

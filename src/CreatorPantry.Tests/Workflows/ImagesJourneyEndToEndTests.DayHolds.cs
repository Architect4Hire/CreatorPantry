using System.Net;
using System.Net.Http.Json;
using CreatorPantry.Domain.Managers.Limits;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Billing.Managers;
using CreatorPantry.Tests.Brand;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CreatorPantry.Tests.Workflows;

/// <summary>
/// The account's day of pictures through the browser's routes (DL.5, B-56): what the provider never made does not use
/// the day, and a generation resumed after a failure charges each picture once.
/// </summary>
public sealed partial class ImagesJourneyEndToEndTests
{
    /// <summary>
    /// The provider refuses before making anything, so the pictures are released — and the same two can be asked for
    /// again the same day. Once they are made, the day is spent: a third is refused with the account's own figures.
    /// </summary>
    [Fact]
    public async Task A_picture_never_made_gives_its_place_in_the_day_back()
    {
        OnDailyCapOf(2);
        using var owner = await _fixture.SignInAsync(A.OwnerEmail, cancellationToken: Ct);

        _generator.Throws(() => new HttpRequestException("refused", inner: null, HttpStatusCode.BadRequest));
        var first = await AskAsync(owner, 2);
        await RunWorkerAsync();

        var failed = await BodyOf(await owner.GetAsync(In(A, $"generated-images/operations/{first}"), Ct));
        Assert.Equal("Failed", failed.GetProperty("status").GetString());
        Assert.Equal(0m, await SpentAsync(A.OwnerEmail));

        // Nothing was made, so both places in the day are free again.
        _generator.Throws(null);
        _generator.Returns(BrandSourceSampleFiles.Png(width: 8, height: 6));
        var second = await AskAsync(owner, 2);
        await RunWorkerAsync();

        var made = await BodyOf(await owner.GetAsync(In(A, $"generated-images/operations/{second}"), Ct));
        Assert.Equal("Succeeded", made.GetProperty("status").GetString());
        Assert.Equal(Credits(2), await SpentAsync(A.OwnerEmail));

        // And now they are used: the day's cap, said as the account's own.
        var refused = await owner.PostAsJsonAsync(
            In(A, "generated-images"), new { promptText = Prompt, variantCount = 1 }, Guid.NewGuid().ToString("N"), Ct);
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        var problem = await BodyOf(refused);
        Assert.Equal(LimitErrorCodes.BudgetExhausted, problem.GetProperty("code").GetString());
        Assert.Equal("User", problem.GetProperty("scope").GetString());
    }

    /// <summary>
    /// The provider fails after two of four pictures; the next pass makes the other two. Four were made and four are
    /// charged — each once, with its own ordinal — and the day counts four.
    /// </summary>
    [Fact]
    public async Task A_resumed_generation_charges_nothing_twice()
    {
        OnDailyCapOf(4);
        using var owner = await _fixture.SignInAsync(A.OwnerEmail, cancellationToken: Ct);

        _generator.Returns(BrandSourceSampleFiles.Png(width: 8, height: 6));
        _generator.ThrowsAfter(2, () => new HttpRequestException("unreachable", inner: null, HttpStatusCode.ServiceUnavailable));
        var operationId = await AskAsync(owner, 4);
        await RunWorkerAsync();

        // Mid-way: two made, nothing charged yet — the hold waits for the pass that finishes.
        Assert.Equal(0m, await SpentAsync(A.OwnerEmail));

        _generator.Throws(null);
        await DueNowAsync(operationId);
        await RunWorkerAsync();

        var made = await BodyOf(await owner.GetAsync(In(A, $"generated-images/operations/{operationId}"), Ct));
        Assert.Equal("Succeeded", made.GetProperty("status").GetString());
        Assert.Equal(4, made.GetProperty("images").GetArrayLength());
        Assert.Equal(Credits(4), await SpentAsync(A.OwnerEmail));
        Assert.Equal([1, 2, 3, 4], (await ChargesAsync(operationId)).Select(charge => charge.AttemptNumber).Order());

        // The day counts the four once: a fifth is refused.
        var refused = await owner.PostAsJsonAsync(
            In(A, "generated-images"), new { promptText = Prompt, variantCount = 1 }, Guid.NewGuid().ToString("N"), Ct);
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
    }

    /// <summary>
    /// One plan, the default, with a daily picture cap of <paramref name="cap"/> — and limits switched on, which the test
    /// host leaves off: the plan's daily cap is a limit and switches off with them (B-56).
    /// </summary>
    private void OnDailyCapOf(int cap)
    {
        var limits = _fixture.Api.Factory.Services.GetRequiredService<IOptions<LimitsOptions>>().Value;
        limits.Enabled = true;
        limits.ImageInFlightPerWorkspace = 50;
        limits.ImageVariantsPerWorkspacePerDay = 50;

        var billing = _fixture.Api.Factory.Services.GetRequiredService<IOptions<BillingOptions>>().Value;

        billing.DefaultPlan = "basic";
        billing.Plans =
        [
            new PlanOption
            {
                Key = "basic",
                Name = "Basic",
                Credits = 6000,
                DailyImageCap = cap,
                Period = PlanAllowancePeriod.Monthly,
                RunningImages = 1,
                QualityClasses = ["draft", "standard", "high"],
            },
        ];
    }

    private async Task<Guid> AskAsync(Tenancy.GatewayClient client, int variants)
    {
        var asked = await client.PostAsJsonAsync(
            In(A, "generated-images"), new { promptText = Prompt, variantCount = variants }, Guid.NewGuid().ToString("N"), Ct);
        Assert.Equal(HttpStatusCode.Accepted, asked.StatusCode);

        return (await BodyOf(asked)).GetProperty("id").GetGuid();
    }

    /// <summary>
    /// Brings a requeued generation's backoff forward, as time passing would: the journey has no clock to move, and
    /// the question here is what the next pass charges, not how long it waits.
    /// </summary>
    private async Task DueNowAsync(Guid operationId)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();

        await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().GeneratedImageOperations
            .IgnoreQueryFilters()
            .Where(operation => operation.Id == operationId)
            .ExecuteUpdateAsync(set => set.SetProperty(operation => operation.AvailableAt, DateTimeOffset.UtcNow.AddSeconds(-1)), Ct);
    }
}

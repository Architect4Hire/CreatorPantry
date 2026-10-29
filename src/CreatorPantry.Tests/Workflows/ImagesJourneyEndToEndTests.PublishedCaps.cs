using System.Net;
using System.Net.Http.Json;
using CreatorPantry.Domain.Managers.Limits;
using CreatorPantry.Domain.Modules.Billing.Facade;
using CreatorPantry.Domain.Modules.Billing.Managers;
using CreatorPantry.Domain.Modules.Media.Managers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CreatorPantry.Tests.Workflows;

/// <summary>
/// The published cap is the enforced cap (DL.6), for every plan the development catalogue publishes: the figure on
/// the plan's card is the figure the account resolves to, exactly that many pictures are admitted in a day through
/// the browser's routes, and the next is refused with that figure as the account's own.
/// </summary>
public sealed partial class ImagesJourneyEndToEndTests
{
    /// <summary>The development catalogue's plans, by key; each case reads its cap from the published card.</summary>
    public static TheoryData<string> PublishedPlans() => ["trial", "creator", "pro", "studio"];

    [Theory]
    [MemberData(nameof(PublishedPlans))]
    public async Task A_published_plans_daily_cap_is_the_cap_its_account_is_held_to(string key)
    {
        OnPublishedPlan(key);
        using var owner = await _fixture.SignInAsync(A.OwnerEmail, cancellationToken: Ct);

        // What the card says, read where a visitor reads it.
        var catalogue = await BodyOf(await owner.GetAsync("/api/v1/plans", Ct));
        var cap = catalogue.GetProperty("plans").EnumerateArray()
            .Single(plan => plan.GetProperty("key").GetString() == key)
            .GetProperty("dailyImageCap")
            .GetInt32();

        // What the account resolves to, which is what admission judges by.
        await using (var scope = _fixture.Api.Factory.Services.CreateAsyncScope())
        {
            var resolved = await scope.ServiceProvider.GetRequiredService<IAccountPlanFacade>()
                .ResolveAsync(await AccountAsync(A.OwnerEmail), Ct);

            Assert.Equal(key, resolved?.Key);
            Assert.Equal(cap, resolved?.DailyImageCap);
        }

        // Exactly the cap is admitted, in requests as large as a request may be.
        for (var left = cap; left > 0; left -= MediaPolicy.MaxVariantsPerOperation)
        {
            await AskAsync(owner, Math.Min(left, MediaPolicy.MaxVariantsPerOperation));
        }

        // And not one more: the day's cap, said as the account's own, with the card's figure.
        var refused = await owner.PostAsJsonAsync(
            In(A, "generated-images"), new { promptText = Prompt, variantCount = 1 }, Guid.NewGuid().ToString("N"), Ct);
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        var problem = await BodyOf(refused);
        Assert.Equal(LimitErrorCodes.BudgetExhausted, problem.GetProperty("code").GetString());
        Assert.Equal("User", problem.GetProperty("scope").GetString());
        Assert.Equal(cap, problem.GetProperty("limit").GetInt32());
    }

    /// <summary>
    /// The development catalogue, published, with <paramref name="key"/> as the plan an account with no row of its own
    /// is on — and limits on, as DL.6 requires of a host that publishes. The workspace's own picture limits and the
    /// request rate are raised out of the way, so the only cap a request can meet is the plan's.
    /// </summary>
    private void OnPublishedPlan(string key)
    {
        var limits = _fixture.Api.Factory.Services.GetRequiredService<IOptions<LimitsOptions>>().Value;
        limits.Enabled = true;
        limits.ImageInFlightPerWorkspace = 200;
        limits.ImageVariantsPerWorkspacePerDay = 200;
        limits.Rates[LimitedOperation.ImageGeneration] = new OperationRateRule
        {
            User = TokenBucketRule.PerMinute(200, 200), Workspace = TokenBucketRule.PerMinute(200, 200),
        };

        var billing = _fixture.Api.Factory.Services.GetRequiredService<IOptions<BillingOptions>>().Value;
        billing.PublishPlans = true;
        billing.DefaultPlan = key;
    }
}

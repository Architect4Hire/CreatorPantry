using CreatorPantry.Domain.Managers.Limits;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Billing.Managers;
using CreatorPantry.Domain.Modules.Media.Facade;
using CreatorPantry.Domain.Modules.Media.Managers;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Media;

/// <summary>
/// Quality classes gated by plan (PRICE-006/007, AP.4.3): a class this host offers is still refused when the
/// asking account's plan does not allow it, and the refusal names the plan that would.
/// </summary>
/// <remarks>
/// The fixture offers Draft, Standard and High (<see cref="ImageQuotaTestSettings"/>) and registers Billing with
/// no catalogue, as every deployment has today; <see cref="OnQualityPlans"/> gives it one through the options
/// instance the module reads, the way <c>OnPlan</c> does for the picture caps.
/// </remarks>
public sealed partial class GeneratedImageWorkerTests
{
    [Fact]
    public async Task A_class_the_plan_allows_is_requested_as_it_was_before_plans()
    {
        OnQualityPlans();

        var requested = await RequestAtAsync(variants: 1, GeneratedImageQualityClass.Draft);

        Assert.Equal(GeneratedImageQualityClass.Draft, requested.QualityClass);
        Assert.Equal(ImageQuotaTestSettings.DraftRate, (await ReservationAsync(requested.Id)).ReservedAmount);
    }

    /// <summary>
    /// <strong>The refusal names the plan that would allow it</strong>, in the sentence and in the extensions,
    /// and nothing is written: no operation, and no hold against the account's allowance.
    /// </summary>
    [Fact]
    public async Task A_class_the_plan_does_not_allow_is_refused_and_names_the_plan_that_would()
    {
        OnQualityPlans();

        var refused = await RequestRawAtAsync(GeneratedImageQualityClass.High);

        Assert.Equal(MediaErrorCodes.GenerationQualityClassForbidden, refused.Error!.Code);
        Assert.Equal("High", refused.Error.Extensions!["qualityClass"]);
        Assert.Equal("creator", refused.Error.Extensions["requiresPlan"]);
        Assert.Equal("Creator", refused.Error.Extensions["requiresPlanName"]);
        Assert.Contains("Creator", refused.Error.Message, StringComparison.Ordinal);
        Assert.Contains("qualityClass", refused.Error.FieldErrors.Keys);

        Assert.Empty(await OperationsAsync(WorkspaceA));
        Assert.Empty(await ReservationsAsync(DefaultAccount));
    }

    /// <summary>The cheapest one, which is the first in catalogue order — the order the landing page renders.</summary>
    [Fact]
    public async Task The_cheapest_plan_that_allows_the_class_is_the_one_named()
    {
        OnQualityPlans();

        // Both "creator" and "studio" allow High; the catalogue lists creator first.
        var refused = await RequestRawAtAsync(GeneratedImageQualityClass.High);

        Assert.Equal("creator", refused.Error!.Extensions!["requiresPlan"]);
    }

    /// <summary>A class no plan reaches is locked with no plan named, rather than naming one that does not exist.</summary>
    [Fact]
    public async Task A_class_no_plan_allows_is_refused_with_no_plan_named()
    {
        Billing.DefaultPlan = "trial";
        Billing.Plans.Add(QualityPlan("trial", "Trial", ["draft", "standard"]));

        var refused = await RequestRawAtAsync(GeneratedImageQualityClass.High);

        Assert.Equal(MediaErrorCodes.GenerationQualityClassForbidden, refused.Error!.Code);
        Assert.Null(refused.Error.Extensions!["requiresPlan"]);
        Assert.Null(refused.Error.Extensions["requiresPlanName"]);
        Assert.DoesNotContain("plan includes", refused.Error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A request that names no class is made at the host's default, which every plan allows — startup refuses a
    /// catalogue where one does not (see <c>PlanLimitsStartupTests</c>), so this path is never plan-refused.
    /// </summary>
    [Fact]
    public async Task A_request_that_names_no_class_is_never_refused_by_the_plan()
    {
        OnQualityPlans();

        var requested = await RequestAsync(WorkspaceA, variants: 1);

        Assert.Equal(GeneratedImageQualityClass.Standard, requested.QualityClass);
    }

    /// <summary>The catalogue's spelling is the landing page's; the comparison ignores case and hyphens.</summary>
    [Fact]
    public async Task A_plan_spelling_a_class_differently_still_allows_it()
    {
        Billing.DefaultPlan = "trial";
        Billing.Plans.Add(QualityPlan("trial", "Trial", ["DRAFT", "  standard  ", "HIGH"]));

        Assert.Equal(
            GeneratedImageQualityClass.High,
            (await RequestAtAsync(variants: 1, GeneratedImageQualityClass.High)).QualityClass);
    }

    /// <summary>
    /// <strong>A replay is never refused.</strong> A creator who paid for High and was then downgraded still gets
    /// the operation their key bought, rather than a 403 for a request that already succeeded.
    /// </summary>
    [Fact]
    public async Task A_replayed_key_is_answered_after_the_plan_stops_allowing_its_class()
    {
        OnQualityPlans(current: "creator");

        await using var scope = ScopeFor(WorkspaceA);
        var facade = scope.ServiceProvider.GetRequiredService<IGeneratedImageGenerationFacade>();
        var ct = TestContext.Current.CancellationToken;
        var request = Request(key: "bought-on-creator") with { QualityClass = GeneratedImageQualityClass.High };

        var first = await facade.RequestAsync(request, ct);
        Assert.True(first.Succeeded, first.Error?.Message);

        await PutOnAsync(DefaultAccount, "trial");

        var replayed = await facade.RequestAsync(request, ct);

        Assert.True(replayed.Succeeded, replayed.Error?.Code);
        Assert.Equal(first.Value!.Id, replayed.Value!.Id);
    }

    /// <summary>
    /// The plan is the account's, not the workspace's (B-47): two members of one workspace on different plans are
    /// each judged by their own.
    /// </summary>
    [Fact]
    public async Task Two_accounts_in_one_workspace_are_judged_by_their_own_plans()
    {
        OnQualityPlans();
        await PutOnAsync(Other, "creator");

        Assert.Equal(
            GeneratedImageQualityClass.High,
            (await RequestAtAsync(variants: 1, GeneratedImageQualityClass.High, account: Other)).QualityClass);

        Assert.Equal(
            MediaErrorCodes.GenerationQualityClassForbidden,
            (await RequestRawAtAsync(GeneratedImageQualityClass.High)).Error!.Code);
    }

    /// <summary>With no plans configured — every deployment today — every class this host offers may be asked for.</summary>
    [Fact]
    public async Task With_no_plans_every_offered_class_may_be_asked_for()
    {
        Assert.Equal(
            GeneratedImageQualityClass.High,
            (await RequestAtAsync(variants: 1, GeneratedImageQualityClass.High)).QualityClass);

        await using var scope = ScopeFor(WorkspaceA);
        var list = await scope.ServiceProvider.GetRequiredService<IGeneratedImageGenerationFacade>()
            .ListQualityClassesAsync(TestContext.Current.CancellationToken);

        Assert.All(list.Value!.Classes, entry =>
        {
            Assert.True(entry.Allowed);
            Assert.Null(entry.RequiresPlan);
            Assert.Null(entry.RequiresPlanName);
        });
    }

    /// <summary>
    /// <strong>Not a limit, so it does not switch off with them.</strong> A plan's picture caps are limits and go
    /// with <c>Limits:Enabled</c> (AP.4.2); which qualities a plan includes is what was bought.
    /// </summary>
    [Fact]
    public async Task The_quality_gate_still_applies_with_limits_switched_off()
    {
        OnQualityPlans();
        Limits.Enabled = false;

        Assert.Equal(
            MediaErrorCodes.GenerationQualityClassForbidden,
            (await RequestRawAtAsync(GeneratedImageQualityClass.High)).Error!.Code);
    }

    /// <summary>A quote is never for a request that would be refused, so it is refused in the same words.</summary>
    [Fact]
    public async Task The_quote_is_refused_for_a_class_the_plan_does_not_allow()
    {
        OnQualityPlans();

        await using var scope = ScopeFor(WorkspaceA);

        var result = await scope.ServiceProvider.GetRequiredService<IGeneratedImageGenerationFacade>()
            .QuoteAsync(2, GeneratedImageQualityClass.High, TestContext.Current.CancellationToken);

        Assert.Equal(MediaErrorCodes.GenerationQualityClassForbidden, result.Error!.Code);
        Assert.Equal("creator", result.Error.Extensions!["requiresPlan"]);
    }

    /// <summary>
    /// The price list keeps every offered class, in order, at its own price — and says which the plan locks and
    /// what would unlock them. A locked class with no price shown would make the plan impossible to weigh.
    /// </summary>
    [Fact]
    public async Task The_classes_list_locks_what_the_plan_does_not_allow_and_keeps_its_price()
    {
        OnQualityPlans();

        await using var scope = ScopeFor(WorkspaceA);

        var list = (await scope.ServiceProvider.GetRequiredService<IGeneratedImageGenerationFacade>()
            .ListQualityClassesAsync(TestContext.Current.CancellationToken)).Value!;

        Assert.Equal(GeneratedImageQualityClass.Standard, list.DefaultClass);
        Assert.Equal(
            [
                (GeneratedImageQualityClass.Draft, ImageQuotaTestSettings.DraftRate, true, (string?)null),
                (GeneratedImageQualityClass.Standard, ImageQuotaTestSettings.RatePerImage, true, null),
                (GeneratedImageQualityClass.High, ImageQuotaTestSettings.HighRate, false, "creator"),
            ],
            list.Classes.Select(entry => (entry.QualityClass, entry.PerPicture, entry.Allowed, entry.RequiresPlan)));

        Assert.Equal("Creator", Assert.Single(list.Classes.Where(entry => !entry.Allowed)).RequiresPlanName);
    }

    /// <summary>Each account reads its own flags from the one list, as it reads its own refusal.</summary>
    [Fact]
    public async Task The_classes_list_is_read_for_the_asking_account()
    {
        OnQualityPlans();
        await PutOnAsync(Other, "creator");

        await using var scope = ScopeFor(WorkspaceA, account: Other);

        var list = (await scope.ServiceProvider.GetRequiredService<IGeneratedImageGenerationFacade>()
            .ListQualityClassesAsync(TestContext.Current.CancellationToken)).Value!;

        Assert.All(list.Classes, entry => Assert.True(entry.Allowed));
    }

    // ---- harness ---------------------------------------------------------------------------------------

    /// <summary>
    /// A catalogue of three nested plans over the classes this fixture offers: the trial stops below High, the
    /// one above it allows High, and the one above that allows a class this host does not offer at all.
    /// </summary>
    private void OnQualityPlans(string current = "trial")
    {
        Billing.DefaultPlan = current;
        Billing.Plans.Add(QualityPlan("trial", "Trial", ["draft", "standard"]));
        Billing.Plans.Add(QualityPlan("creator", "Creator", ["draft", "standard", "high"]));
        Billing.Plans.Add(QualityPlan("studio", "Studio", ["draft", "standard", "high", "high-2k"]));
    }

    private static PlanOption QualityPlan(string key, string name, string[] classes) => new()
    {
        Key = key,
        Name = name,
        Credits = 40000,
        DailyImageCap = 100,
        RunningImages = 3,
        Period = PlanAllowancePeriod.Monthly,
        QualityClasses = [.. classes],
    };

    private async Task<OperationResult<GeneratedImageOperationServiceModel>> RequestRawAtAsync(
        GeneratedImageQualityClass quality, string account = DefaultAccount)
    {
        await using var scope = ScopeFor(WorkspaceA, account: account);

        return await scope.ServiceProvider.GetRequiredService<IGeneratedImageGenerationFacade>().RequestAsync(
            Request() with { QualityClass = quality }, TestContext.Current.CancellationToken);
    }
}
